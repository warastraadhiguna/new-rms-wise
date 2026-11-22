<?php

use App\Models\User;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Support\Facades\Gate;
use Illuminate\Support\Facades\Route;

beforeEach(function () {
    Route::middleware(['web', 'auth', 'hq', 'permission:master.edit'])->get('/_test/master', fn () => 'ok');
    Route::middleware(['web', 'auth', 'hq', 'permission:purchase.view,purchase.edit'])->get('/_test/purchase', fn () => 'ok');
    Route::middleware(['web', 'auth', 'hq', 'can:report.view'])->get('/_test/report', fn () => 'ok');
});

function actingWith(array $permissions)
{
    return test()->actingAs(User::find(makeUser($permissions)->id));
}

it('lets a route require a permission', function () {
    actingWith(['hq.access'])->get('/_test/master')->assertForbidden();
    actingWith(['hq.access', 'master.edit'])->get('/_test/master')->assertOk();
});

it('accepts any one of several permissions', function () {
    actingWith(['hq.access', 'purchase.edit'])->get('/_test/purchase')->assertOk();
    actingWith(['hq.access', 'stock.view'])->get('/_test/purchase')->assertForbidden();
});

it('maps gates to permissions and "*" grants everything', function () {
    actingWith(['hq.access', 'report.view'])->get('/_test/report')->assertOk();
    actingWith(['hq.access'])->get('/_test/report')->assertForbidden();
    actingWith(['*'])->get('/_test/master')->assertOk();

    $owner = User::find(makeUser(['*'])->id);
    expect(Gate::forUser($owner)->allows('anything.at.all'))->toBeTrue();
});

it('never lets a user without hq.access through, whatever else they have', function () {
    actingWith(['master.edit'])->get('/_test/master')->assertRedirect('/login');
});

it('merges roles and ignores deleted roles and roles at other nodes', function () {
    $user = makeUser(['hq.access']);
    $writer = app(MasterDataWriter::class);
    $ctx = WriteContext::system();
    $role = fn (array $permissions) => $writer->create('roles', [
        'code' => 'X'.strtoupper(substr(uniqid(), -12)), 'name' => 'x', 'permissions' => json_encode($permissions),
    ], $ctx);

    $writer->create('user_roles', ['user_id' => $user->id, 'role_id' => $role(['master.edit']), 'node_id' => null], $ctx);
    $deleted = $writer->create('user_roles', ['user_id' => $user->id, 'role_id' => $role(['purchase.edit']), 'node_id' => null], $ctx);
    $writer->delete('user_roles', $deleted, $ctx);
    $entity = $writer->create('entities', ['code' => 'E'.substr(uniqid(), -10), 'name' => 'e'], $ctx);
    $branch = $writer->create('branches', ['entity_id' => $entity, 'code' => 'B'.substr(uniqid(), -10), 'name' => 'b'], $ctx);
    $store = $writer->create('nodes', ['branch_id' => $branch, 'code' => 'T'.substr(uniqid(), -8), 'name' => 'toko', 'type' => 'STORE'], $ctx);
    $writer->create('user_roles', ['user_id' => $user->id, 'role_id' => $role(['report.view']), 'node_id' => $store], $ctx);

    expect(User::find($user->id)->permissions()->names())->toBe(['hq.access', 'master.edit']);
});
