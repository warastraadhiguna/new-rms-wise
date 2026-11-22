<?php

use App\Models\User;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Str;

/**
 * A user of its own for one test (never a shared seed user): unique username, bcrypt password (cost from
 * BCRYPT_ROUNDS), one role with exactly these permissions at all nodes.
 *
 * @param  list<string>  $permissions
 * @return object{id: string, username: string, password: string}
 */
function makeUser(array $permissions = ['hq.access'], string $password = 'rahasia-123', bool $active = true, bool $deleted = false): object
{
    $writer = app(MasterDataWriter::class);
    $context = WriteContext::system();
    $username = 'u'.substr(str_replace('-', '', (string) Str::uuid7()), -16);

    $userId = $writer->create('users', [
        'username' => $username,
        'password_hash' => password_hash($password, PASSWORD_BCRYPT, ['cost' => 4]),
        'full_name' => 'Pengguna '.$username,
        'is_active' => $active,
    ], $context);
    $roleId = $writer->create('roles', [
        'code' => 'R'.strtoupper(substr(str_replace('-', '', (string) Str::uuid7()), -20)),
        'name' => 'Peran uji',
        'permissions' => json_encode($permissions),
    ], $context);
    $writer->create('user_roles', ['user_id' => $userId, 'role_id' => $roleId, 'node_id' => null], $context);
    if ($deleted) {
        $writer->delete('users', $userId, $context);
    }

    return (object) ['id' => $userId, 'username' => $username, 'password' => $password];
}

function hqNodeId(): string
{
    return (string) DB::table('nodes')->where('code', config('pos.hq_node_code'))->value('id');
}

/** @param list<string> $permissions */
function editor(array $permissions = ['hq.access', 'master.edit']): User
{
    return User::findOrFail(makeUser($permissions)->id);
}

/** A unique upper-case code (12 characters after the prefix). */
function uid(string $prefix = ''): string
{
    return $prefix.strtoupper(substr(str_replace('.', '', uniqid('', true)), -12));
}

function lastAudit(string $table, string $id): ?object
{
    return DB::table('audit_logs')->where('table_name', $table)->where('record_id', $id)->orderByDesc('id')->first();
}

function versionOf(string $table, string $id): int
{
    return (int) DB::table($table)->where('id', $id)->value('version');
}

function newBranch(): string
{
    $writer = app(MasterDataWriter::class);
    $entity = $writer->create('entities', ['code' => uid('E'), 'name' => 'PT Uji'], WriteContext::system());

    return $writer->create('branches', ['entity_id' => $entity, 'code' => uid('B'), 'name' => 'Cabang Uji'], WriteContext::system());
}
