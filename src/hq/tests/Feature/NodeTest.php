<?php

use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Support\Facades\DB;
use Inertia\Testing\AssertableInertia as Assert;

/*
| Toko dan Gudang (12b): nodes and their locations. A new store gets DISPLAY (sellable) and DAMAGED, a new warehouse
| STORAGE and DAMAGED, in the same transaction, all through the master data writer.
*/

function nodeCode(string $prefix): string
{
    return $prefix.substr(uid(), -8);                 // nodes.code is VARCHAR(10)
}

it('creates a store with DISPLAY and DAMAGED locations', function () {
    $this->actingAs(editor());
    $code = nodeCode('T');
    $branch = newBranch();

    $this->post('/master-data/nodes', ['code' => strtolower($code), 'name' => 'Toko Baru', 'type' => 'STORE', 'branch_id' => $branch, 'address' => 'Jl. Uji 1'])
        ->assertSessionHasNoErrors();

    $node = DB::table('nodes')->where('code', $code)->first();
    expect($node->type)->toBe('STORE')->and($node->branch_id)->toBe($branch)
        ->and(lastAudit('nodes', $node->id)->action)->toBe('CREATE');

    $locations = DB::table('locations')->where('node_id', $node->id)->orderBy('code')->get();
    expect($locations->pluck('code')->all())->toBe(['DAMAGED', 'DISPLAY'])
        ->and($locations->pluck('type')->all())->toBe(['DAMAGED', 'DISPLAY'])
        ->and($locations->map(fn ($l) => (bool) $l->is_sellable)->all())->toBe([false, true])
        ->and(lastAudit('locations', $locations[0]->id)->action)->toBe('CREATE')
        ->and((int) $locations[0]->version)->toBeGreaterThan((int) $node->version);
});

it('creates a warehouse with STORAGE and DAMAGED; HQ nodes are not created here', function () {
    $this->actingAs(editor());
    $code = nodeCode('G');
    $this->post('/master-data/nodes', ['code' => $code, 'name' => 'Gudang', 'type' => 'WAREHOUSE', 'branch_id' => newBranch()])->assertSessionHasNoErrors();
    $node = (string) DB::table('nodes')->where('code', $code)->value('id');
    expect(DB::table('locations')->where('node_id', $node)->orderBy('code')->pluck('code')->all())->toBe(['DAMAGED', 'STORAGE']);

    $this->post('/master-data/nodes', ['code' => nodeCode('H'), 'name' => 'Pusat 2', 'type' => 'HQ', 'branch_id' => newBranch()])->assertSessionHasErrors('type');
});

it('validates node codes and keeps the type when updating', function () {
    $this->actingAs(editor());
    $branch = newBranch();
    $code = nodeCode('T');
    $this->post('/master-data/nodes', ['code' => $code, 'name' => 'A', 'type' => 'STORE', 'branch_id' => $branch])->assertSessionHasNoErrors();
    $id = (string) DB::table('nodes')->where('code', $code)->value('id');

    $this->post('/master-data/nodes', ['code' => $code, 'name' => 'B', 'type' => 'STORE', 'branch_id' => $branch])
        ->assertSessionHasErrors(['code' => 'Kode sudah dipakai (termasuk node yang sudah dihapus).']);
    $this->post('/master-data/nodes', ['code' => 'TERLALUPANJANG', 'name' => 'B', 'type' => 'STORE', 'branch_id' => $branch])->assertSessionHasErrors('code');
    $this->post('/master-data/nodes', ['code' => 'TK-9', 'name' => 'B', 'type' => 'STORE', 'branch_id' => $branch])->assertSessionHasErrors('code');

    $this->put("/master-data/nodes/{$id}", ['code' => $code, 'name' => 'Toko A', 'type' => 'WAREHOUSE', 'branch_id' => $branch, 'is_active' => false])->assertSessionHasNoErrors();
    $row = DB::table('nodes')->where('id', $id)->first();
    expect($row->type)->toBe('STORE')->and($row->name)->toBe('Toko A')->and((bool) $row->is_active)->toBeFalse()
        ->and(lastAudit('nodes', $id)->action)->toBe('UPDATE');
});

it('adds and edits locations; codes are unique per node and damaged stock is never sellable', function () {
    $this->actingAs(editor());
    $code = nodeCode('T');
    $this->post('/master-data/nodes', ['code' => $code, 'name' => 'A', 'type' => 'STORE', 'branch_id' => newBranch()])->assertSessionHasNoErrors();
    $node = (string) DB::table('nodes')->where('code', $code)->value('id');

    $this->post("/master-data/nodes/{$node}/locations", ['code' => 'rak-a', 'name' => 'Rak A', 'type' => 'STORAGE'])->assertSessionHasNoErrors();
    $rak = DB::table('locations')->where('node_id', $node)->where('code', 'RAK-A')->first();
    expect($rak)->not->toBeNull()->and((bool) $rak->is_sellable)->toBeFalse();

    $this->post("/master-data/nodes/{$node}/locations", ['code' => 'DISPLAY', 'name' => 'X', 'type' => 'DISPLAY'])
        ->assertSessionHasErrors(['code' => 'Kode lokasi sudah dipakai di node ini.']);
    $this->post("/master-data/nodes/{$node}/locations", ['code' => 'RUSAK2', 'name' => 'X', 'type' => 'DAMAGED', 'is_sellable' => true])
        ->assertSessionHasErrors(['is_sellable' => 'Lokasi barang rusak tidak boleh dijual.']);

    $this->put("/master-data/nodes/{$node}/locations/{$rak->id}", ['code' => 'RAK-A', 'name' => 'Rak Depan', 'type' => 'DISPLAY', 'is_sellable' => true, 'is_active' => false])
        ->assertSessionHasNoErrors();
    $after = DB::table('locations')->where('id', $rak->id)->first();
    expect($after->name)->toBe('Rak Depan')->and((bool) $after->is_sellable)->toBeTrue()->and((bool) $after->is_active)->toBeFalse()
        ->and(lastAudit('locations', $rak->id)->action)->toBe('UPDATE');

    // Another node may use the same location code.
    $other = nodeCode('T');
    $this->post('/master-data/nodes', ['code' => $other, 'name' => 'B', 'type' => 'STORE', 'branch_id' => newBranch()])->assertSessionHasNoErrors();
    $otherId = (string) DB::table('nodes')->where('code', $other)->value('id');
    $this->post("/master-data/nodes/{$otherId}/locations", ['code' => 'RAK-A', 'name' => 'Rak A', 'type' => 'STORAGE'])->assertSessionHasNoErrors();
    $this->put("/master-data/nodes/{$otherId}/locations/{$rak->id}", ['code' => 'X', 'name' => 'X', 'type' => 'STORAGE'])->assertNotFound();
});

it('lists nodes with their location count and shows a node page', function () {
    $this->actingAs(editor(['hq.access', 'master.view']));
    $code = nodeCode('T');
    $writer = app(MasterDataWriter::class);
    $node = $writer->create('nodes', ['code' => $code, 'name' => 'Lihat', 'type' => 'STORE', 'branch_id' => newBranch()], WriteContext::system());
    $writer->create('locations', ['node_id' => $node, 'code' => 'DISPLAY', 'name' => 'Display', 'type' => 'DISPLAY', 'is_sellable' => true], WriteContext::system());

    $this->get("/master-data/nodes?q={$code}")->assertOk()
        ->assertInertia(fn (Assert $page) => $page->component('master-data/nodes')->has('items.data', 1)->where('items.data.0.locations', 1));
    $this->get("/master-data/nodes/{$node}")->assertOk()
        ->assertInertia(fn (Assert $page) => $page->component('master-data/node')->where('node.code', $code)->has('locations', 1));
    $this->post('/master-data/nodes', [])->assertForbidden();
});
