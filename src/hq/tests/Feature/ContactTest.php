<?php

use App\Support\MasterData\CodeGenerator;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Support\Facades\DB;
use Inertia\Testing\AssertableInertia as Assert;

/*
| Pelanggan and Supplier (contacts), the supplier's sales reps and supplied products (12b). Every write goes through the
| master data writer (version + audit).
*/

function newSupplier(): string
{
    return app(MasterDataWriter::class)->create('contacts', ['code' => uid('S'), 'name' => 'Supplier Uji', 'is_supplier' => true], WriteContext::system());
}

function newProductRow(): string
{
    $writer = app(MasterDataWriter::class);
    $unit = $writer->create('units', ['code' => uid('U'), 'name' => 'Satuan'], WriteContext::system());
    $product = $writer->create('products', ['sku' => uid('SKU'), 'name' => 'Barang Uji', 'base_unit_id' => $unit, 'status' => 'ACTIVE'], WriteContext::system());
    $pu = $writer->create('product_units', ['product_id' => $product, 'unit_id' => $unit, 'conversion' => '1', 'is_default_sale' => true], WriteContext::system());
    $writer->create('product_barcodes', ['product_unit_id' => $pu, 'barcode' => uid('899')], WriteContext::system());

    return $product;
}

it('creates a customer with an automatic code, credit limit and term, then updates it', function () {
    $this->actingAs(editor());
    $name = 'Pelanggan '.uid();

    $this->post('/master-data/customers', ['name' => $name, 'phone' => '0812', 'credit_limit' => '2500000', 'payment_term_days' => 30])
        ->assertSessionHasNoErrors();

    $row = DB::table('contacts')->where('name', $name)->first();
    expect($row->code)->toMatch('/^C\d{6}$/')
        ->and((bool) $row->is_customer)->toBeTrue()
        ->and((bool) $row->is_supplier)->toBeFalse()
        ->and($row->credit_limit)->toBe('2500000.0000')
        ->and((int) $row->payment_term_days)->toBe(30)
        ->and(lastAudit('contacts', $row->id)->action)->toBe('CREATE');

    $before = versionOf('contacts', $row->id);
    $this->put("/master-data/customers/{$row->id}", ['code' => '', 'name' => $name, 'credit_limit' => '', 'is_active' => false])
        ->assertSessionHasNoErrors();
    $after = DB::table('contacts')->where('id', $row->id)->first();
    expect($after->code)->toBe($row->code)                         // empty code keeps the old one
        ->and($after->credit_limit)->toBeNull()
        ->and((bool) $after->is_active)->toBeFalse()
        ->and((int) $after->version)->toBeGreaterThan($before)
        ->and(lastAudit('contacts', $row->id)->action)->toBe('UPDATE');
});

it('numbers automatic codes after the highest existing one, deleted contacts included', function () {
    $next = CodeGenerator::next('contacts', 'code', 'C');
    $number = (int) substr($next, 1);
    $id = app(MasterDataWriter::class)->create('contacts', ['code' => $next, 'name' => 'X', 'is_customer' => true], WriteContext::system());
    app(MasterDataWriter::class)->delete('contacts', $id, WriteContext::system());

    expect(CodeGenerator::next('contacts', 'code', 'C'))->toBe('C'.str_pad((string) ($number + 1), 6, '0', STR_PAD_LEFT))
        ->and(CodeGenerator::next('contacts', 'code', 'ZQ'))->toBe('ZQ000001');
});

it('keeps codes unique for ever and validates the customer fields', function () {
    $this->actingAs(editor());
    $code = uid('C');
    $id = app(MasterDataWriter::class)->create('contacts', ['code' => $code, 'name' => 'Lama', 'is_customer' => true], WriteContext::system());
    app(MasterDataWriter::class)->delete('contacts', $id, WriteContext::system());

    $this->post('/master-data/customers', ['code' => strtolower($code), 'name' => 'Baru'])
        ->assertSessionHasErrors(['code' => 'Kode sudah dipakai (termasuk kontak yang sudah dihapus).']);
    $this->post('/master-data/customers', ['name' => 'X', 'credit_limit' => '-1', 'payment_term_days' => 400, 'email' => 'bukan-email'])
        ->assertSessionHasErrors(['credit_limit', 'payment_term_days', 'email']);
});

it('creates a supplier with an S code and bank account; a contact may be both', function () {
    $this->actingAs(editor());
    $name = 'Supplier '.uid();
    $this->post('/master-data/suppliers', ['name' => $name, 'bank_account' => 'BCA 123 a.n. PT X', 'is_customer' => true])->assertSessionHasNoErrors();

    $row = DB::table('contacts')->where('name', $name)->first();
    expect($row->code)->toMatch('/^S\d{6}$/')
        ->and((bool) $row->is_supplier)->toBeTrue()
        ->and((bool) $row->is_customer)->toBeTrue()
        ->and($row->bank_account)->toBe('BCA 123 a.n. PT X');

    $this->get('/master-data/customers?q='.urlencode($name))->assertOk()
        ->assertInertia(fn (Assert $page) => $page->component('master-data/contacts')->where('kind', 'customers')->has('items.data', 1));
    $this->get('/master-data/suppliers?q='.urlencode($name).'&active=0')->assertOk()
        ->assertInertia(fn (Assert $page) => $page->has('items.data', 0));
});

it('lists, searches and needs master permissions', function () {
    $name = 'Cari '.uid();
    app(MasterDataWriter::class)->create('contacts', ['code' => uid('C'), 'name' => $name, 'is_customer' => true], WriteContext::system());

    $this->actingAs(editor(['hq.access', 'master.view']));
    $this->get('/master-data/customers?q='.urlencode($name))->assertOk()
        ->assertInertia(fn (Assert $page) => $page->has('items.data', 1)->where('items.data.0.name', $name));
    $this->post('/master-data/customers', ['name' => 'X'])->assertForbidden();

    $this->actingAs(editor(['hq.access']));
    $this->get('/master-data/customers')->assertForbidden();
    $this->get('/master-data/others')->assertNotFound();
});

it('manages a supplier\'s sales reps; deleting a rep releases its products', function () {
    $this->actingAs(editor());
    $supplier = newSupplier();

    $this->post("/master-data/suppliers/{$supplier}/reps", ['name' => 'Budi', 'phone' => '0813'])->assertSessionHasNoErrors();
    $rep = (string) DB::table('sales_reps')->where('supplier_id', $supplier)->value('id');
    $this->put("/master-data/suppliers/{$supplier}/reps/{$rep}", ['name' => 'Budi S', 'is_active' => false])->assertSessionHasNoErrors();
    expect(DB::table('sales_reps')->where('id', $rep)->value('name'))->toBe('Budi S')
        ->and(lastAudit('sales_reps', $rep)->action)->toBe('UPDATE');

    $product = newProductRow();
    $sku = (string) DB::table('products')->where('id', $product)->value('sku');
    $this->post("/master-data/suppliers/{$supplier}/products", ['product' => $sku, 'sales_rep_id' => $rep, 'supplier_sku' => 'X-1'])->assertSessionHasNoErrors();

    $this->delete("/master-data/suppliers/{$supplier}/reps/{$rep}")->assertSessionHasNoErrors();
    expect(DB::table('sales_reps')->where('id', $rep)->value('deleted_at'))->not->toBeNull()
        ->and(DB::table('supplier_products')->where('supplier_id', $supplier)->value('sales_rep_id'))->toBeNull();

    $this->get("/master-data/suppliers/{$supplier}")->assertOk()
        ->assertInertia(fn (Assert $page) => $page->component('master-data/supplier')->has('reps', 0)->has('products', 1)->where('products.0.supplier_sku', 'X-1'));
});

it('adds supplied products by SKU or barcode, refuses duplicates, and brings a removed one back', function () {
    $this->actingAs(editor());
    $supplier = newSupplier();
    $product = newProductRow();
    $barcode = (string) DB::table('product_barcodes as b')->join('product_units as u', 'u.id', '=', 'b.product_unit_id')->where('u.product_id', $product)->value('b.barcode');

    $this->post("/master-data/suppliers/{$supplier}/products", ['product' => $barcode])->assertSessionHasNoErrors();
    $item = DB::table('supplier_products')->where('supplier_id', $supplier)->where('product_id', $product)->first();
    expect(lastAudit('supplier_products', $item->id)->action)->toBe('CREATE');

    $this->post("/master-data/suppliers/{$supplier}/products", ['product' => $barcode])
        ->assertSessionHasErrors(['product' => 'Barang ini sudah ada di daftar supplier ini.']);
    $this->post("/master-data/suppliers/{$supplier}/products", ['product' => 'TIDAK-ADA'])
        ->assertSessionHasErrors(['product' => 'Barang dengan SKU atau barcode TIDAK-ADA tidak ditemukan.']);

    $this->delete("/master-data/suppliers/{$supplier}/products/{$item->id}")->assertSessionHasNoErrors();
    expect(DB::table('supplier_products')->where('id', $item->id)->value('deleted_at'))->not->toBeNull();

    $this->post("/master-data/suppliers/{$supplier}/products", ['product' => $barcode, 'supplier_sku' => 'BARU'])->assertSessionHasNoErrors();
    $back = DB::table('supplier_products')->where('id', $item->id)->first();
    expect($back->deleted_at)->toBeNull()
        ->and($back->supplier_sku)->toBe('BARU')
        ->and(lastAudit('supplier_products', $item->id)->action)->toBe('RESTORE');
});

it('refuses a sales rep of another supplier and pages of non-suppliers', function () {
    $this->actingAs(editor());
    $a = newSupplier();
    $b = newSupplier();
    $repOfB = app(MasterDataWriter::class)->create('sales_reps', ['supplier_id' => $b, 'name' => 'Rep B'], WriteContext::system());
    $sku = (string) DB::table('products')->where('id', newProductRow())->value('sku');

    $this->post("/master-data/suppliers/{$a}/products", ['product' => $sku, 'sales_rep_id' => $repOfB])->assertSessionHasErrors('sales_rep_id');
    $this->put("/master-data/suppliers/{$a}/reps/{$repOfB}", ['name' => 'X'])->assertNotFound();

    $customer = app(MasterDataWriter::class)->create('contacts', ['code' => uid('C'), 'name' => 'Pelanggan', 'is_customer' => true], WriteContext::system());
    $this->get("/master-data/suppliers/{$customer}")->assertNotFound();
});
