<?php

use App\Auth\OwnerGuard;
use App\Auth\PermissionCatalog;
use App\Models\User;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Support\Facades\DB;
use Inertia\Testing\AssertableInertia as Assert;

/*
| Pengguna and Role (12b): admin-set passwords (bcrypt, never shown), role assignments per node or all nodes, the role
| page with permissions from PermissionCatalog, and the guards (no self-deactivation, always one active OWNER).
*/

function admin(): User
{
    return editor(['hq.access', 'users.manage']);
}

function ownerRoleId(): string
{
    return (string) (DB::table('roles')->where('code', OwnerGuard::ROLE_CODE)->value('id')
        ?? app(MasterDataWriter::class)->create('roles', ['code' => OwnerGuard::ROLE_CODE, 'name' => 'Pemilik', 'permissions' => json_encode(['*'])], WriteContext::system()));
}

/** A new active user holding OWNER for all nodes; returns [user id, assignment id]. */
function makeOwner(): array
{
    $user = makeUser(['hq.access']);
    $assignment = app(MasterDataWriter::class)->create('user_roles', ['user_id' => $user->id, 'role_id' => ownerRoleId(), 'node_id' => null], WriteContext::system());

    return [$user->id, $assignment];
}

/**
 * Runs $test with $keep as the only active OWNER, inside a transaction that is rolled back afterwards, so the other
 * owners in the shared test database are untouched.
 */
function withOnlyOwner(string $keep, Closure $test): void
{
    DB::beginTransaction();
    try {
        DB::table('users')->whereIn('id', DB::table('user_roles')->where('role_id', ownerRoleId())->whereNull('node_id')->pluck('user_id'))
            ->where('id', '!=', $keep)->update(['is_active' => false]);
        expect(app(OwnerGuard::class)->activeOwners())->toBe(1);
        $test();
    } finally {
        DB::rollBack();
    }
}

it('creates a user with a bcrypt password that is never shown, then updates the user', function () {
    $this->actingAs(admin());
    $username = 'kasir'.strtolower(uid());

    $this->post('/pengaturan/users', [
        'username' => strtoupper($username), 'full_name' => 'Kasir Satu', 'phone' => '0812 3456',
        'password' => 'rahasia-123', 'password_confirmation' => 'rahasia-123',
    ])->assertSessionHasNoErrors();

    $row = DB::table('users')->where('username', $username)->first();
    expect($row)->not->toBeNull()
        ->and($row->password_hash)->toStartWith('$2y$')
        ->and(password_verify('rahasia-123', $row->password_hash))->toBeTrue()
        ->and($row->phone)->toBe('0812 3456')
        ->and(lastAudit('users', $row->id)->action)->toBe('CREATE');

    $this->get('/pengaturan/users?q='.$username)->assertOk()
        ->assertInertia(fn (Assert $page) => $page->component('settings/users')->has('items.data', 1)
            ->missing('items.data.0.password_hash')->where('items.data.0.username', $username));

    $before = versionOf('users', $row->id);
    $this->put("/pengaturan/users/{$row->id}", ['username' => $username, 'full_name' => 'Kasir Dua', 'is_active' => false])->assertSessionHasNoErrors();
    expect(DB::table('users')->where('id', $row->id)->value('full_name'))->toBe('Kasir Dua')
        ->and(versionOf('users', $row->id))->toBeGreaterThan($before)
        ->and(DB::table('users')->where('id', $row->id)->value('password_hash'))->toBe($row->password_hash);
});

it('validates username, phone and password rules', function () {
    $this->actingAs(admin());
    $taken = makeUser(['hq.access'], deleted: true)->username;

    $this->post('/pengaturan/users', ['username' => $taken, 'full_name' => 'X', 'password' => 'rahasia-123', 'password_confirmation' => 'rahasia-123'])
        ->assertSessionHasErrors(['username' => 'Username sudah dipakai (termasuk pengguna yang sudah dihapus).']);
    $this->post('/pengaturan/users', ['username' => 'ada spasi', 'full_name' => 'X', 'phone' => 'abc', 'password' => 'rahasia-123', 'password_confirmation' => 'rahasia-123'])
        ->assertSessionHasErrors(['username', 'phone']);
    $this->post('/pengaturan/users', ['username' => 'u'.strtolower(uid()), 'full_name' => 'X', 'password' => '1234567', 'password_confirmation' => '1234567'])
        ->assertSessionHasErrors('password');
    $this->post('/pengaturan/users', ['username' => 'u'.strtolower(uid()), 'full_name' => 'X', 'password' => 'rahasia-123', 'password_confirmation' => 'lain-lain-1'])
        ->assertSessionHasErrors('password');
});

it('lets an admin set a new password; the user logs in with it', function () {
    $this->actingAs(admin());
    $user = makeUser(['hq.access']);

    $this->put("/pengaturan/users/{$user->id}/password", ['password' => 'baru-sekali', 'password_confirmation' => 'baru-sekali'])->assertSessionHasNoErrors();
    expect(lastAudit('users', $user->id)->action)->toBe('UPDATE');

    auth()->logout();
    $this->post('/login', ['username' => $user->username, 'password' => 'baru-sekali'])->assertRedirect('/dashboard');
});

it('refuses to let an admin deactivate themselves', function () {
    $me = admin();
    $this->actingAs($me);

    $this->put("/pengaturan/users/{$me->id}", ['username' => $me->username, 'full_name' => $me->full_name, 'is_active' => false])
        ->assertSessionHasErrors(['is_active' => 'Anda tidak bisa menonaktifkan akun Anda sendiri.']);
    expect((bool) DB::table('users')->where('id', $me->id)->value('is_active'))->toBeTrue();
});

it('assigns roles per node or for all nodes and revokes them', function () {
    $this->actingAs(admin());
    $user = makeUser(['hq.access']);
    $role = app(MasterDataWriter::class)->create('roles', ['code' => uid('R'), 'name' => 'Kasir', 'permissions' => json_encode(['sale.create'])], WriteContext::system());

    $this->post("/pengaturan/users/{$user->id}/roles", ['role_id' => $role, 'node_id' => app(MasterDataWriter::class)->create('nodes', ['code' => 'T'.substr(uid(), -8), 'name' => 'Toko', 'type' => 'STORE', 'branch_id' => newBranch()], WriteContext::system())])->assertSessionHasNoErrors();
    $this->post("/pengaturan/users/{$user->id}/roles", ['role_id' => $role, 'node_id' => ''])->assertSessionHasNoErrors();
    $this->post("/pengaturan/users/{$user->id}/roles", ['role_id' => $role, 'node_id' => ''])
        ->assertSessionHasErrors(['role_id' => 'Role ini sudah diberikan untuk node tersebut.']);

    $all = (string) DB::table('user_roles')->where('user_id', $user->id)->where('role_id', $role)->whereNull('node_id')->value('id');
    $this->delete("/pengaturan/users/{$user->id}/roles/{$all}")->assertSessionHasNoErrors();
    expect(DB::table('user_roles')->where('id', $all)->value('deleted_at'))->not->toBeNull()
        ->and(lastAudit('user_roles', $all)->action)->toBe('DELETE');
});

it('never leaves the system without an active OWNER', function () {
    $this->actingAs(admin());
    [$owner, $assignment] = makeOwner();
    $username = (string) DB::table('users')->where('id', $owner)->value('username');

    withOnlyOwner($owner, function () use ($owner, $assignment, $username) {
        $this->put("/pengaturan/users/{$owner}", ['username' => $username, 'full_name' => 'Pemilik', 'is_active' => false])
            ->assertSessionHasErrors('is_active');
        expect((bool) DB::table('users')->where('id', $owner)->value('is_active'))->toBeTrue();

        $this->delete("/pengaturan/users/{$owner}/roles/{$assignment}")->assertSessionHasErrors('role');
        expect(DB::table('user_roles')->where('id', $assignment)->value('deleted_at'))->toBeNull();
    });

    // With a second owner both changes are fine.
    makeOwner();
    $this->delete("/pengaturan/users/{$owner}/roles/{$assignment}")->assertSessionHasNoErrors();
});

it('saves role permissions from the catalog, including discount.max', function () {
    $this->actingAs(admin());
    $code = uid('KASIR');

    $this->post('/pengaturan/roles', ['code' => strtolower($code), 'name' => 'Kasir', 'permissions' => ['sale.create', 'shift.open'], 'discount_max' => '10.50'])
        ->assertSessionHasNoErrors();
    $row = DB::table('roles')->where('code', $code)->first();
    expect(json_decode($row->permissions, true))->toBe(['sale.create', 'shift.open', 'discount.max:10.5'])
        ->and(lastAudit('roles', $row->id)->action)->toBe('CREATE');

    $this->put("/pengaturan/roles/{$row->id}", ['code' => $code, 'name' => 'Kasir', 'permissions' => ['*', 'sale.create'], 'discount_max' => ''])->assertSessionHasNoErrors();
    expect(json_decode((string) DB::table('roles')->where('id', $row->id)->value('permissions'), true))->toBe(['*']);

    $this->post('/pengaturan/roles', ['code' => uid('R'), 'name' => 'X', 'permissions' => ['sale.terbang']])->assertSessionHasErrors('permissions');
    $this->post('/pengaturan/roles', ['code' => uid('R'), 'name' => 'X', 'permissions' => ['discount.max:5']])->assertSessionHasErrors('permissions');
    $this->post('/pengaturan/roles', ['code' => uid('R'), 'name' => 'X', 'permissions' => [], 'discount_max' => '100.01'])->assertSessionHasErrors('discount_max');
    $this->post('/pengaturan/roles', ['code' => $code, 'name' => 'X', 'permissions' => []])
        ->assertSessionHasErrors(['code' => 'Kode sudah dipakai (termasuk role yang sudah dihapus).']);

    $this->get('/pengaturan/roles')->assertOk()->assertInertia(fn (Assert $page) => $page->component('settings/roles')
        ->has('catalog', count(PermissionCatalog::groups()))->where('ownerCode', 'OWNER'));
});

it('keeps OWNER at "*" and refuses deleting OWNER or a role still in use', function () {
    $this->actingAs(admin());
    $owner = ownerRoleId();

    $this->put("/pengaturan/roles/{$owner}", ['code' => 'LAIN', 'name' => 'Pemilik Toko', 'permissions' => ['sale.create']])->assertSessionHasNoErrors();
    $row = DB::table('roles')->where('id', $owner)->first();
    expect($row->code)->toBe('OWNER')->and($row->name)->toBe('Pemilik Toko')->and(json_decode($row->permissions, true))->toBe(['*']);

    $this->delete("/pengaturan/roles/{$owner}")->assertSessionHasErrors(['delete' => 'Role OWNER tidak bisa dihapus.']);

    $used = app(MasterDataWriter::class)->create('roles', ['code' => uid('R'), 'name' => 'Dipakai', 'permissions' => '[]'], WriteContext::system());
    app(MasterDataWriter::class)->create('user_roles', ['user_id' => makeUser()->id, 'role_id' => $used, 'node_id' => null], WriteContext::system());
    $this->delete("/pengaturan/roles/{$used}")->assertSessionHasErrors('delete');

    $free = app(MasterDataWriter::class)->create('roles', ['code' => uid('R'), 'name' => 'Bebas', 'permissions' => '[]'], WriteContext::system());
    $this->delete("/pengaturan/roles/{$free}")->assertSessionHasNoErrors();
    expect(DB::table('roles')->where('id', $free)->value('deleted_at'))->not->toBeNull();
});

it('needs users.manage for users and roles', function () {
    $this->actingAs(editor(['hq.access', 'master.edit']));
    $this->get('/pengaturan/users')->assertForbidden();
    $this->get('/pengaturan/roles')->assertForbidden();
    $this->post('/pengaturan/roles', [])->assertForbidden();
    $this->get('/pengaturan')->assertOk();
});
