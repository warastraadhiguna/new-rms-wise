<?php

use App\Models\User;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Str;
use Inertia\Testing\AssertableInertia as Assert;

/*
| Master data barang (12a): units, categories, brands, products, selling units, barcodes and prices. Every change goes
| through the master data writer: the row's version rises and audit_logs records it.
*/

function newUnit(string $code = '', bool $decimal = false): string
{
    return app(MasterDataWriter::class)->create('units', ['code' => $code ?: uid('U'), 'name' => 'Satuan uji', 'allow_decimal' => $decimal], WriteContext::system());
}

function setUniform(?bool $value): void
{
    DB::table('settings')->whereNull('branch_id')->where('setting_key', 'pricing.uniform')->delete();
    if ($value !== null) {
        app(MasterDataWriter::class)->create('settings', ['branch_id' => null, 'setting_key' => 'pricing.uniform', 'setting_value' => json_encode($value)], WriteContext::system());
    }
}

/** Creates a product through the HQ web and returns its id. */
function createProduct(array $overrides = []): string
{
    $sku = $overrides['sku'] ?? uid('SKU');
    test()->post('/master-data/products', array_merge([
        'sku' => $sku, 'name' => 'Teh Botol '.$sku, 'base_unit_id' => newUnit(), 'status' => 'ACTIVE',
        'track_expiry' => true, 'allow_qty_multiplier' => true, 'barcodes' => [uid('899')], 'price' => '4000',
    ], $overrides))->assertSessionHasNoErrors();

    return (string) DB::table('products')->where('sku', $sku)->value('id');
}

function baseUnitOf(string $product): string
{
    return (string) DB::table('product_units as pu')->join('products as p', 'p.id', '=', 'pu.product_id')
        ->where('pu.product_id', $product)->whereColumn('pu.unit_id', 'p.base_unit_id')->value('pu.id');
}

beforeEach(fn () => setUniform(null));

// ---- units, categories, brands ----------------------------------------------------------------------

it('creates, updates and soft-deletes a unit with versions and audit', function () {
    $this->actingAs(editor());
    $code = uid('U');

    $this->post('/master-data/units', ['code' => strtolower($code), 'name' => 'Lusin', 'allow_decimal' => false])->assertRedirect();
    $id = (string) DB::table('units')->where('code', $code)->value('id');      // stored upper-case
    $created = versionOf('units', $id);
    expect(lastAudit('units', $id)->action)->toBe('CREATE');

    $this->put("/master-data/units/{$id}", ['code' => $code, 'name' => 'Lusin (12)', 'allow_decimal' => true])->assertRedirect();
    expect(versionOf('units', $id))->toBeGreaterThan($created)
        ->and(lastAudit('units', $id)->action)->toBe('UPDATE')
        ->and(json_decode(lastAudit('units', $id)->old_values, true)['name'])->toBe('Lusin');

    $this->delete("/master-data/units/{$id}")->assertRedirect();
    expect(DB::table('units')->where('id', $id)->value('deleted_at'))->not->toBeNull()
        ->and(lastAudit('units', $id)->action)->toBe('DELETE');

    // a code is unique for ever: the deleted row keeps it
    $this->post('/master-data/units', ['code' => $code, 'name' => 'Lagi'])->assertSessionHasErrors(['code' => 'Kode satuan sudah dipakai (termasuk satuan yang sudah dihapus).']);
});

it('lists and searches units and refuses to delete a unit in use', function () {
    $this->actingAs(editor());
    $product = createProduct();
    $base = (string) DB::table('products')->where('id', $product)->value('base_unit_id');
    $code = (string) DB::table('units')->where('id', $base)->value('code');

    $this->get('/master-data/units?q='.$code)->assertOk()->assertInertia(fn (Assert $page) => $page
        ->component('master-data/units')->where('items.data.0.code', $code)->where('items.total', 1));
    $this->delete("/master-data/units/{$base}")->assertSessionHasErrors(['delete' => 'Satuan masih dipakai barang; tidak bisa dihapus.']);
    expect(DB::table('units')->where('id', $base)->value('deleted_at'))->toBeNull();
});

it('keeps category names unique per parent and protects used categories and parents', function () {
    $this->actingAs(editor());
    $name = 'Minuman '.uid();

    $this->post('/master-data/categories', ['name' => $name])->assertRedirect();
    $parent = (string) DB::table('categories')->where('name', $name)->value('id');
    $this->post('/master-data/categories', ['name' => $name])->assertSessionHasErrors(['name' => 'Nama kategori sudah ada.']);
    $this->post('/master-data/categories', ['name' => 'Teh', 'parent_id' => $parent])->assertRedirect();
    $child = (string) DB::table('categories')->where('parent_id', $parent)->value('id');
    expect(lastAudit('categories', $child)->action)->toBe('CREATE');

    $this->delete("/master-data/categories/{$parent}")->assertSessionHasErrors(['delete' => 'Kategori masih punya sub-kategori; tidak bisa dihapus.']);
    createProduct(['category_id' => $child]);
    $this->delete("/master-data/categories/{$child}")->assertSessionHasErrors(['delete' => 'Kategori masih dipakai barang; tidak bisa dihapus.']);

    $v = versionOf('categories', $parent);
    $this->put("/master-data/categories/{$parent}", ['name' => $name.' Dingin'])->assertRedirect();
    expect(versionOf('categories', $parent))->toBeGreaterThan($v)->and(lastAudit('categories', $parent)->action)->toBe('UPDATE');
});

it('creates, renames and deletes brands with unique names', function () {
    $this->actingAs(editor());
    $name = 'Sosro '.uid();

    $this->post('/master-data/brands', ['name' => $name])->assertRedirect();
    $id = (string) DB::table('brands')->where('name', $name)->value('id');
    $this->post('/master-data/brands', ['name' => $name])->assertSessionHasErrors(['name' => 'Nama merek sudah ada.']);
    $v = versionOf('brands', $id);
    $this->put("/master-data/brands/{$id}", ['name' => $name.' Baru'])->assertRedirect();
    expect(versionOf('brands', $id))->toBeGreaterThan($v);

    $this->delete("/master-data/brands/{$id}")->assertRedirect();
    expect(lastAudit('brands', $id)->action)->toBe('DELETE');
    $this->post('/master-data/brands', ['name' => $name.' Baru'])->assertRedirect();     // a deleted name may be used again
});

// ---- products -------------------------------------------------------------------------------------------

it('creates a product with its base unit (conversion 1), barcodes and first price, all audited', function () {
    $this->actingAs(editor());
    $barcode = uid('899');

    $id = createProduct(['barcodes' => [$barcode], 'price' => '4500', 'min_stock' => '12.5']);

    $product = DB::table('products')->where('id', $id)->first();
    $unit = DB::table('product_units')->where('product_id', $id)->first();
    expect($product->min_stock)->toBe('12.5000')
        ->and((int) $product->track_expiry)->toBe(1)
        ->and($unit->unit_id)->toBe($product->base_unit_id)
        ->and($unit->conversion)->toBe('1.0000')
        ->and((int) $unit->is_default_sale)->toBe(1)
        ->and(DB::table('product_barcodes')->where('product_unit_id', $unit->id)->value('barcode'))->toBe($barcode)
        ->and(DB::table('prices')->where('product_unit_id', $unit->id)->value('price'))->toBe('4500.0000');
    foreach (['products' => $id, 'product_units' => $unit->id] as $table => $rowId) {
        expect(lastAudit($table, $rowId)->action)->toBe('CREATE')->and(versionOf($table, $rowId))->toBeGreaterThan(0);
    }

    $this->get("/master-data/products/{$id}/edit")->assertOk()->assertInertia(fn (Assert $page) => $page
        ->component('master-data/products/form')
        ->where('product.units.0.is_base', true)
        ->where('product.units.0.barcodes.0.barcode', $barcode)
        ->where('product.units.0.prices.0.price', '4500.0000')
        ->where('product.units.0.prices.0.status', 'current'));
});

it('keeps SKU and barcodes unique', function () {
    $this->actingAs(editor());
    $sku = uid('SKU');
    $barcode = uid('899');
    createProduct(['sku' => $sku, 'barcodes' => [$barcode]]);

    $this->post('/master-data/products', ['sku' => $sku, 'name' => 'X', 'base_unit_id' => newUnit(), 'status' => 'ACTIVE'])
        ->assertSessionHasErrors(['sku' => 'SKU sudah dipakai (termasuk barang yang sudah dihapus).']);
    $this->post('/master-data/products', ['sku' => uid('SKU'), 'name' => 'X', 'base_unit_id' => newUnit(), 'status' => 'ACTIVE', 'barcodes' => [$barcode]])
        ->assertSessionHasErrors(['barcode' => "Barcode {$barcode} sudah dipakai barang lain."]);
    expect(DB::table('product_barcodes')->where('barcode', $barcode)->count())->toBe(1);
});

it('updates a product but never its base unit', function () {
    $this->actingAs(editor());
    $id = createProduct();
    $product = DB::table('products')->where('id', $id)->first();
    $v = (int) $product->version;
    $payload = ['sku' => $product->sku, 'name' => 'Nama baru', 'base_unit_id' => $product->base_unit_id, 'status' => 'INACTIVE',
        'shelf_location' => 'Rak B-2', 'track_expiry' => false, 'allow_qty_multiplier' => false];

    $this->put("/master-data/products/{$id}", $payload)->assertRedirect();
    $this->put("/master-data/products/{$id}", ['base_unit_id' => newUnit()] + $payload)->assertSessionHasErrors('base_unit_id');

    $after = DB::table('products')->where('id', $id)->first();
    expect($after->name)->toBe('Nama baru')->and($after->status)->toBe('INACTIVE')->and($after->shelf_location)->toBe('Rak B-2')
        ->and((int) $after->allow_qty_multiplier)->toBe(0)->and((int) $after->version)->toBeGreaterThan($v)
        ->and(lastAudit('products', $id)->action)->toBe('UPDATE')
        ->and($after->base_unit_id)->toBe($product->base_unit_id);
});

it('lists products with search by code, name or barcode, filters and paging', function () {
    $this->actingAs(editor());
    $marker = uid('M');
    $writer = app(MasterDataWriter::class);
    $brand = $writer->create('brands', ['name' => 'Merek '.$marker], WriteContext::system());
    $barcode = uid('899');
    $a = createProduct(['name' => "Kopi {$marker}", 'brand_id' => $brand, 'barcodes' => [$barcode]]);
    createProduct(['name' => "Gula {$marker}", 'status' => 'INACTIVE', 'barcodes' => []]);

    $find = fn (string $query) => $this->get("/master-data/products?{$query}")->assertOk();

    $find("q={$marker}")->assertInertia(fn (Assert $p) => $p->component('master-data/products/index')->where('items.total', 2));
    $find("q={$barcode}")->assertInertia(fn (Assert $p) => $p->where('items.total', 1)->where('items.data.0.id', $a)->where('items.data.0.price', '4000.0000'));
    $find("q={$marker}&brand_id={$brand}")->assertInertia(fn (Assert $p) => $p->where('items.total', 1));
    $find("q={$marker}&status=INACTIVE")->assertInertia(fn (Assert $p) => $p->where('items.total', 1)->where('items.data.0.status', 'INACTIVE'));
    $find('q=&page=1')->assertInertia(fn (Assert $p) => $p->where('items.per_page', 25));
});

it('refuses to delete a product that was ever used and offers Nonaktif instead', function (string $usage) {
    $this->actingAs(editor());
    $id = createProduct();
    $writer = app(MasterDataWriter::class);
    $node = $writer->create('nodes', ['branch_id' => newBranch(), 'code' => 'N'.substr(uid(), -9), 'name' => 'Toko', 'type' => 'STORE'], WriteContext::system());
    $location = $writer->create('locations', ['node_id' => $node, 'code' => 'DISPLAY', 'name' => 'Display', 'type' => 'DISPLAY', 'is_sellable' => true], WriteContext::system());
    match ($usage) {
        'stock ledger' => DB::table('stock_ledger')->insert([
            'id' => (string) Str::uuid7(), 'node_id' => $node, 'location_id' => $location, 'product_id' => $id,
            'base_qty' => '1.0000', 'doc_type' => 'ADJUSTMENT', 'doc_id' => (string) Str::uuid7(), 'occurred_at' => now('UTC'),
        ]),
        'stock' => DB::table('stock_balances')->insert([
            'location_id' => $location, 'product_id' => $id, 'expiry_key' => '9999-12-31', 'base_qty' => '3.0000',
        ]),
        'cost' => DB::table('product_costs')->insert([
            'entity_id' => DB::table('branches')->where('id', DB::table('nodes')->where('id', $node)->value('branch_id'))->value('entity_id'),
            'product_id' => $id, 'base_qty' => '0', 'total_value' => '0', 'average_cost' => '3500',
        ]),
    };

    $this->delete("/master-data/products/{$id}")
        ->assertSessionHasErrors(['delete', 'delete_used']);
    expect(session('errors')->first('delete'))->toContain('tidak bisa dihapus')
        ->and(DB::table('products')->where('id', $id)->value('deleted_at'))->toBeNull();

    $v = versionOf('products', $id);
    $this->post("/master-data/products/{$id}/deactivate")->assertRedirect();
    expect(DB::table('products')->where('id', $id)->value('status'))->toBe('INACTIVE')
        ->and(versionOf('products', $id))->toBeGreaterThan($v)
        ->and(lastAudit('products', $id)->action)->toBe('UPDATE');
})->with(['stock ledger', 'stock', 'cost']);

it('soft-deletes a product that was never used, with its units and barcodes', function () {
    $this->actingAs(editor());
    $id = createProduct();
    $unit = baseUnitOf($id);

    $this->delete("/master-data/products/{$id}")->assertRedirect('/master-data/products');

    expect(DB::table('products')->where('id', $id)->value('deleted_at'))->not->toBeNull()
        ->and(DB::table('product_units')->where('id', $unit)->value('deleted_at'))->not->toBeNull()
        ->and(DB::table('product_barcodes')->where('product_unit_id', $unit)->whereNull('deleted_at')->count())->toBe(0)
        ->and(lastAudit('products', $id)->action)->toBe('DELETE');
    $this->get("/master-data/products/{$id}/edit")->assertNotFound();
});

// ---- selling units and barcodes ---------------------------------------------------------------------------

it('adds a selling unit with conversion > 0 and barcodes; the base unit keeps conversion 1', function () {
    $this->actingAs(editor());
    $id = createProduct();
    $box = newUnit();
    $barcode = uid('BX');

    $this->post("/master-data/products/{$id}/units", ['unit_id' => $box, 'conversion' => '0', 'barcodes' => []])->assertSessionHasErrors('conversion');
    $this->post("/master-data/products/{$id}/units", ['unit_id' => $box, 'conversion' => '24', 'is_default_sale' => true, 'barcodes' => [$barcode]])->assertRedirect();
    $this->post("/master-data/products/{$id}/units", ['unit_id' => $box, 'conversion' => '12'])->assertSessionHasErrors(['unit_id' => 'Satuan ini sudah ada pada barang ini.']);

    $boxUnit = (string) DB::table('product_units')->where('product_id', $id)->where('unit_id', $box)->value('id');
    $base = baseUnitOf($id);
    expect(DB::table('product_units')->where('id', $boxUnit)->value('conversion'))->toBe('24.0000')
        ->and((int) DB::table('product_units')->where('id', $boxUnit)->value('is_default_sale'))->toBe(1)
        ->and((int) DB::table('product_units')->where('id', $base)->value('is_default_sale'))->toBe(0)   // only one default sale unit
        ->and(DB::table('product_barcodes')->where('barcode', $barcode)->value('product_unit_id'))->toBe($boxUnit);

    $this->put("/master-data/products/{$id}/units/{$base}", ['conversion' => '2'])->assertSessionHasErrors(['conversion' => 'Satuan dasar wajib punya konversi 1.']);
    $this->delete("/master-data/products/{$id}/units/{$base}")->assertSessionHasErrors(['delete' => 'Satuan dasar tidak bisa dihapus.']);
    $v = versionOf('product_units', $boxUnit);
    $this->put("/master-data/products/{$id}/units/{$boxUnit}", ['conversion' => '20'])->assertRedirect();
    expect(versionOf('product_units', $boxUnit))->toBeGreaterThan($v)->and(lastAudit('product_units', $boxUnit)->action)->toBe('UPDATE');
});

it('brings back a removed unit and a removed barcode instead of a duplicate row', function () {
    $this->actingAs(editor());
    $id = createProduct(['barcodes' => []]);
    $other = createProduct(['barcodes' => []]);
    $box = newUnit();
    $barcode = uid('BX');
    $this->post("/master-data/products/{$id}/units", ['unit_id' => $box, 'conversion' => '24', 'barcodes' => [$barcode]]);
    $boxUnit = (string) DB::table('product_units')->where('product_id', $id)->where('unit_id', $box)->value('id');
    $barcodeId = (string) DB::table('product_barcodes')->where('barcode', $barcode)->value('id');

    $this->delete("/master-data/products/{$id}/units/{$boxUnit}")->assertRedirect();
    $this->post("/master-data/products/{$id}/units", ['unit_id' => $box, 'conversion' => '12'])->assertRedirect();
    expect(DB::table('product_units')->where('product_id', $id)->where('unit_id', $box)->count())->toBe(1)
        ->and(DB::table('product_units')->where('id', $boxUnit)->value('deleted_at'))->toBeNull()
        ->and(DB::table('product_units')->where('id', $boxUnit)->value('conversion'))->toBe('12.0000')
        ->and(lastAudit('product_units', $boxUnit)->action)->toBe('RESTORE');

    // the barcode removed with the unit can go to another product
    $this->post("/master-data/products/{$other}/units/".baseUnitOf($other).'/barcodes', ['barcode' => $barcode])->assertRedirect();
    expect(DB::table('product_barcodes')->where('barcode', $barcode)->count())->toBe(1)
        ->and(DB::table('product_barcodes')->where('id', $barcodeId)->value('product_unit_id'))->toBe(baseUnitOf($other))
        ->and(lastAudit('product_barcodes', $barcodeId)->action)->toBe('RESTORE');
    $this->post("/master-data/products/{$id}/units/{$boxUnit}/barcodes", ['barcode' => $barcode])
        ->assertSessionHasErrors(['barcode' => "Barcode {$barcode} sudah dipakai barang lain."]);
});

// ---- prices ---------------------------------------------------------------------------------------------

it('adds prices as new rows: history, current and future, wholesale tiers', function () {
    $this->actingAs(editor());
    $id = createProduct(['price' => '4000']);
    $unit = baseUnitOf($id);
    $tomorrow = now('Asia/Jakarta')->addDay()->format('Y-m-d');
    $today = now('Asia/Jakarta')->format('Y-m-d');
    $first = (string) DB::table('prices')->where('product_unit_id', $unit)->value('id');

    $this->post("/master-data/products/{$id}/units/{$unit}/prices", ['price' => '4200', 'min_qty' => '1', 'effective_from' => $today])->assertRedirect();
    $this->post("/master-data/products/{$id}/units/{$unit}/prices", ['price' => '4500', 'min_qty' => '1', 'effective_from' => $tomorrow])->assertRedirect();
    $this->post("/master-data/products/{$id}/units/{$unit}/prices", ['price' => '3800', 'min_qty' => '12', 'effective_from' => $today])->assertRedirect();
    $this->post("/master-data/products/{$id}/units/{$unit}/prices", ['price' => '1', 'min_qty' => '1', 'effective_from' => now('Asia/Jakarta')->subDay()->format('Y-m-d')])
        ->assertSessionHasErrors(['effective_from' => 'Tanggal berlaku tidak boleh di masa lalu (riwayat harga tidak diubah).']);
    $this->post("/master-data/products/{$id}/units/{$unit}/prices", ['price' => '1', 'min_qty' => '0', 'effective_from' => $today])->assertSessionHasErrors('min_qty');

    expect(DB::table('prices')->where('product_unit_id', $unit)->whereNull('deleted_at')->count())->toBe(4)
        ->and(DB::table('prices')->where('id', $first)->value('price'))->toBe('4000.0000');     // the old price is untouched

    $this->get("/master-data/products/{$id}/edit")->assertInertia(fn (Assert $page) => $page->where('product.units.0.prices', function ($prices) {
        $byPrice = collect($prices)->mapWithKeys(fn ($p) => [$p['price'] => $p['status']]);

        return $byPrice->all() == ['4500.0000' => 'future', '4200.0000' => 'current', '3800.0000' => 'current', '4000.0000' => 'history'];
    }));
    $this->get('/master-data/products?q='.DB::table('products')->where('id', $id)->value('sku'))
        ->assertInertia(fn (Assert $p) => $p->where('items.data.0.price', '4200.0000'));

    // a future price may be withdrawn (audited soft delete); a price in effect stays as history
    $future = (string) DB::table('prices')->where('product_unit_id', $unit)->where('price', '4500')->value('id');
    $current = (string) DB::table('prices')->where('product_unit_id', $unit)->where('price', '4200')->value('id');
    $this->delete("/master-data/products/{$id}/prices/{$current}")->assertSessionHasErrors(['delete' => 'Harga yang sudah berlaku tidak bisa dihapus; buat harga baru.']);
    $this->delete("/master-data/products/{$id}/prices/{$future}")->assertRedirect();
    expect(lastAudit('prices', $future)->action)->toBe('DELETE')->and(lastAudit('prices', $current)->action)->toBe('CREATE');
});

it('allows branch prices only while pricing.uniform is false', function () {
    $this->actingAs(editor());
    $id = createProduct();
    $unit = baseUnitOf($id);
    $branch = newBranch();
    $today = now('Asia/Jakarta')->format('Y-m-d');

    $this->post("/master-data/products/{$id}/units/{$unit}/prices", ['price' => '4800', 'min_qty' => '1', 'effective_from' => $today, 'branch_id' => $branch])->assertRedirect();
    expect(DB::table('prices')->where('product_unit_id', $unit)->where('branch_id', $branch)->value('price'))->toBe('4800.0000');
    $this->get("/master-data/products/{$id}/edit")->assertInertia(fn (Assert $p) => $p->where('pricingUniform', false));

    setUniform(true);

    $this->post("/master-data/products/{$id}/units/{$unit}/prices", ['price' => '4900', 'min_qty' => '1', 'effective_from' => $today, 'branch_id' => $branch])
        ->assertSessionHasErrors(['branch_id' => 'Harga per cabang tidak dipakai: setting harga seragam (pricing.uniform) aktif.']);
    $this->post("/master-data/products/{$id}/units/{$unit}/prices", ['price' => '4900', 'min_qty' => '1', 'effective_from' => $today])->assertRedirect();
    $this->get("/master-data/products/{$id}/edit")->assertInertia(fn (Assert $p) => $p->where('pricingUniform', true));
});

// ---- permissions ----------------------------------------------------------------------------------------

it('needs master.view to read and master.edit to change master data', function () {
    $viewer = User::find(makeUser(['hq.access', 'master.view'])->id);
    $nobody = User::find(makeUser(['hq.access'])->id);

    $this->actingAs($nobody)->get('/master-data/products')->assertForbidden();
    $this->actingAs($viewer)->get('/master-data/products')->assertOk();
    $this->actingAs($viewer)->get('/master-data/units')->assertOk();
    $this->actingAs($viewer)->get('/master-data')->assertOk()->assertInertia(fn (Assert $p) => $p->component('master-data/index'));
    $this->actingAs($viewer)->post('/master-data/units', ['code' => uid('U'), 'name' => 'x'])->assertForbidden();
    $this->actingAs($viewer)->get('/master-data/products/create')->assertForbidden();
    $this->actingAs(editor())->get('/master-data/products/create')->assertOk()
        ->assertInertia(fn (Assert $p) => $p->component('master-data/products/form')->where('product', null)->has('units')->has('statuses', 2));
});
