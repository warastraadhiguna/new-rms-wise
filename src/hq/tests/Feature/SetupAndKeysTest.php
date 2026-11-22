<?php

use App\Auth\OwnerGuard;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use App\Support\Setup\SystemSetup;
use Illuminate\Database\QueryException;
use Illuminate\Support\Facades\Artisan;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Str;

/*
| Migration 0011 (unique keys over nullable columns) and pos:setup (system data, idempotent).
*/

function rawSetting(?string $branch, string $key): void
{
    DB::table('settings')->insert(['id' => (string) Str::uuid7(), 'branch_id' => $branch, 'setting_key' => $key, 'setting_value' => '1']);
}

function isDuplicate(Closure $insert): bool
{
    try {
        $insert();
    } catch (QueryException $e) {
        return ($e->errorInfo[1] ?? null) === 1062;
    }

    return false;
}

it('refuses a second global row of the same setting (NULL branch), and per branch', function () {
    $key = 'test.'.strtolower(uid());
    rawSetting(null, $key);
    expect(isDuplicate(fn () => rawSetting(null, $key)))->toBeTrue();

    $branch = newBranch();
    rawSetting($branch, $key);                                   // a branch row next to the global one is fine
    expect(isDuplicate(fn () => rawSetting($branch, $key)))->toBeTrue()
        ->and(DB::table('settings')->where('setting_key', $key)->count())->toBe(2);
});

it('allows one active role assignment per user, role and node (NULL = all nodes); revoked rows stay', function () {
    $writer = app(MasterDataWriter::class);
    $ctx = WriteContext::system();
    $user = makeUser()->id;
    $role = $writer->create('roles', ['code' => uid('R'), 'name' => 'Uji', 'permissions' => '[]'], $ctx);

    $first = $writer->create('user_roles', ['user_id' => $user, 'role_id' => $role, 'node_id' => null], $ctx);
    expect(isDuplicate(fn () => $writer->create('user_roles', ['user_id' => $user, 'role_id' => $role, 'node_id' => null], $ctx)))->toBeTrue();

    $writer->delete('user_roles', $first, $ctx);
    $second = $writer->create('user_roles', ['user_id' => $user, 'role_id' => $role, 'node_id' => null], $ctx);
    $writer->delete('user_roles', $second, $ctx);
    $writer->create('user_roles', ['user_id' => $user, 'role_id' => $role, 'node_id' => null], $ctx);
    expect(DB::table('user_roles')->where('user_id', $user)->where('role_id', $role)->count())->toBe(3);
});

it('allows one active price per unit, branch (NULL = all), min qty and start; the web says so', function () {
    $this->actingAs(editor());
    $sku = uid('SKU');
    $this->post('/master-data/products', ['sku' => $sku, 'name' => 'Harga '.$sku, 'base_unit_id' => app(MasterDataWriter::class)->create('units', ['code' => uid('U'), 'name' => 'U'], WriteContext::system()),
        'status' => 'ACTIVE', 'barcodes' => [], 'price' => ''])->assertSessionHasNoErrors();
    $product = (string) DB::table('products')->where('sku', $sku)->value('id');
    $unit = (string) DB::table('product_units')->where('product_id', $product)->value('id');
    $future = now('Asia/Jakarta')->addDays(10)->format('Y-m-d');

    $this->post("/master-data/products/{$product}/units/{$unit}/prices", ['price' => '5000', 'min_qty' => '1', 'effective_from' => $future])->assertSessionHasNoErrors();
    $this->post("/master-data/products/{$product}/units/{$unit}/prices", ['price' => '5500', 'min_qty' => '1', 'effective_from' => $future])
        ->assertSessionHasErrors('effective_from');
    $this->post("/master-data/products/{$product}/units/{$unit}/prices", ['price' => '4500', 'min_qty' => '12', 'effective_from' => $future])->assertSessionHasNoErrors();

    $row = DB::table('prices')->where('product_unit_id', $unit)->where('min_qty', '1.0000')->first();
    expect(isDuplicate(fn () => DB::table('prices')->insert([
        'id' => (string) Str::uuid7(), 'product_unit_id' => $unit, 'branch_id' => null, 'price' => '1.0000', 'min_qty' => '1.0000',
        'effective_from' => $row->effective_from, 'created_by' => $row->created_by,
    ])))->toBeTrue();

    // Withdrawn (soft-deleted) prices do not block a new one at the same start.
    $this->delete("/master-data/products/{$product}/prices/{$row->id}")->assertSessionHasNoErrors();
    $this->post("/master-data/products/{$product}/units/{$unit}/prices", ['price' => '5500', 'min_qty' => '1', 'effective_from' => $future])->assertSessionHasNoErrors();
});

it('pos:setup creates the system data once and keeps what admins changed', function () {
    expect(Artisan::call('pos:setup', ['--no-interaction' => true]))->toBe(0);
    $version = (int) DB::table('master_version')->value('last_version');

    foreach (array_keys(SystemSetup::PAYMENT_METHODS) as $code) {
        expect(DB::table('payment_methods')->where('code', $code)->exists())->toBeTrue();
    }
    foreach (SystemSetup::ROLES as $code => [, $permissions]) {
        expect(DB::table('roles')->where('code', $code)->exists())->toBeTrue();
    }
    expect((bool) DB::table('payment_methods')->where('code', 'CASH')->value('is_cash'))->toBeTrue()
        ->and((bool) DB::table('payment_methods')->where('code', 'CREDIT')->value('is_credit'))->toBeTrue()
        ->and(DB::table('reasons')->where('type', 'RETURN')->where('name', 'Barang rusak')->whereNull('deleted_at')->exists())->toBeTrue()
        ->and(DB::table('settings')->whereNull('branch_id')->where('setting_key', 'returns.window_days')->exists())->toBeTrue()
        ->and(DB::table('units')->where('code', 'PCS')->exists())->toBeTrue();

    // An admin edits a role: a second run changes nothing (no new version at all).
    $cashier = (string) DB::table('roles')->where('code', 'CASHIER')->value('id');
    DB::table('roles')->where('id', $cashier)->update(['permissions' => '["sale.create"]']);
    expect(Artisan::call('pos:setup', ['--no-interaction' => true]))->toBe(0)
        ->and(Artisan::output())->toContain('nothing to add')
        ->and((int) DB::table('master_version')->value('last_version'))->toBe($version)
        ->and(DB::table('roles')->where('id', $cashier)->value('permissions'))->toBe('["sale.create"]');
});

it('pos:setup creates the first OWNER when none is active', function () {
    DB::beginTransaction();
    try {
        DB::table('users')->update(['is_active' => false]);
        expect(app(OwnerGuard::class)->activeOwners())->toBe(0);
        $username = 'owner'.strtolower(uid());

        expect(Artisan::call('pos:setup', ['--no-interaction' => true, '--owner-username' => $username, '--owner-password' => 'pendek']))->toBe(1);
        expect(Artisan::call('pos:setup', ['--no-interaction' => true, '--owner-username' => $username, '--owner-password' => 'rahasia-123']))->toBe(0);

        $user = DB::table('users')->where('username', $username)->first();
        expect(password_verify('rahasia-123', $user->password_hash))->toBeTrue()
            ->and(app(OwnerGuard::class)->activeOwners())->toBe(1);
    } finally {
        DB::rollBack();
    }
});
