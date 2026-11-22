<?php

use App\Models\User;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Database\QueryException;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Process;

function writer(): MasterDataWriter
{
    return app(MasterDataWriter::class);
}

function unitCode(): string
{
    return 'U'.strtoupper(substr(uniqid(), -10)).random_int(10, 99);
}

function auditOf(string $table, string $id): array
{
    return DB::table('audit_logs')->where('table_name', $table)->where('record_id', $id)->orderBy('id')->get()->all();
}

it('creates a row with a UUID v7 and the next global version, and audits it', function () {
    $before = writer()->currentVersion();

    $id = writer()->create('units', ['code' => $code = unitCode(), 'name' => 'Lusin'], new WriteContext(null, '10.0.0.7'));

    $row = DB::table('units')->where('id', $id)->first();
    expect($id)->toMatch('/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/')
        ->and((int) $row->version)->toBe($before + 1)
        ->and(writer()->currentVersion())->toBe($before + 1)
        ->and($row->deleted_at)->toBeNull();

    [$audit] = auditOf('units', $id);
    expect($audit->action)->toBe('CREATE')
        ->and($audit->old_values)->toBeNull()
        ->and(json_decode($audit->new_values, true))->toMatchArray(['code' => $code, 'name' => 'Lusin', 'version' => $before + 1])
        ->and($audit->ip_address)->toBe('10.0.0.7');
});

it('raises the version on update and records old and new values', function () {
    $id = writer()->create('units', ['code' => unitCode(), 'name' => 'Lama'], WriteContext::system());
    $created = (int) DB::table('units')->where('id', $id)->value('version');

    $version = writer()->update('units', $id, ['name' => 'Baru', 'allow_decimal' => 1], WriteContext::system());

    expect($version)->toBeGreaterThan($created)
        ->and((int) DB::table('units')->where('id', $id)->value('version'))->toBe($version);
    $update = auditOf('units', $id)[1];
    expect($update->action)->toBe('UPDATE')
        ->and(json_decode($update->old_values, true))->toMatchArray(['name' => 'Lama', 'allow_decimal' => 0, 'version' => $created])
        ->and(json_decode($update->new_values, true))->toMatchArray(['name' => 'Baru', 'allow_decimal' => 1, 'version' => $version]);
});

it('deletes softly: deleted_at and a new version, the row stays', function () {
    $id = writer()->create('units', ['code' => unitCode(), 'name' => 'Hapus'], WriteContext::system());

    $version = writer()->delete('units', $id, WriteContext::system());

    $row = DB::table('units')->where('id', $id)->first();
    expect($row)->not->toBeNull()
        ->and($row->deleted_at)->not->toBeNull()
        ->and((int) $row->version)->toBe($version)
        ->and(collect(auditOf('units', $id))->last()->action)->toBe('DELETE')
        ->and(fn () => writer()->delete('units', $id, WriteContext::system()))->toThrow(RuntimeException::class)
        ->and(fn () => writer()->update('units', $id, ['name' => 'x'], WriteContext::system()))->toThrow(RuntimeException::class);
});

it('records the logged-in user and the request IP', function () {
    $user = makeUser();
    $this->actingAs(User::find($user->id));
    $this->app->instance('request', Request::create('/master-data', 'POST', server: ['REMOTE_ADDR' => '192.168.1.20']));

    $id = writer()->create('units', ['code' => unitCode(), 'name' => 'Audit']);

    $audit = auditOf('units', $id)[0];
    expect($audit->user_id)->toBe($user->id)
        ->and($audit->ip_address)->toBe('192.168.1.20');
});

it('rolls back the version and the row together when a write fails', function () {
    $code = unitCode();
    writer()->create('units', ['code' => $code, 'name' => 'Satu'], WriteContext::system());
    $before = writer()->currentVersion();

    expect(fn () => writer()->create('units', ['code' => $code, 'name' => 'Duplikat'], WriteContext::system()))
        ->toThrow(QueryException::class);

    expect(writer()->currentVersion())->toBe($before)
        ->and(DB::table('units')->where('code', $code)->count())->toBe(1);
});

it('refuses tables without id/version/deleted_at and columns it owns', function () {
    expect(fn () => writer()->create('audit_logs', ['table_name' => 'x'], WriteContext::system()))->toThrow(InvalidArgumentException::class)
        ->and(fn () => writer()->create('units', ['code' => unitCode(), 'name' => 'x', 'version' => 9], WriteContext::system()))->toThrow(InvalidArgumentException::class)
        ->and(fn () => writer()->create('units', ['code' => unitCode(), 'name' => 'x', 'nope' => 1], WriteContext::system()))->toThrow(InvalidArgumentException::class)
        ->and(fn () => writer()->create('units; DROP TABLE x', [], WriteContext::system()))->toThrow(InvalidArgumentException::class);
});

it('gives parallel writes different versions', function () {
    $before = writer()->currentVersion();
    $codes = array_map(fn () => unitCode(), range(1, 6));

    $results = Process::pool(function ($pool) use ($codes) {
        foreach ($codes as $code) {
            $pool->path(base_path())
                ->env(['DB_DATABASE' => DB::connection()->getDatabaseName(), 'APP_ENV' => 'testing'])
                ->command([PHP_BINARY, 'tests/Support/write-master.php', $code]);
        }
    })->start()->wait();

    $versions = [];
    foreach ($results as $result) {
        expect($result->successful())->toBeTrue($result->errorOutput())
            ->and(trim($result->output()))->toMatch('/^\d+$/');
        $versions[] = (int) trim($result->output());
    }

    sort($versions);
    expect(array_unique($versions))->toHaveCount(6)
        ->and(min($versions))->toBeGreaterThan($before)
        ->and(writer()->currentVersion())->toBeGreaterThanOrEqual($before + 6)
        ->and(DB::table('units')->whereIn('code', $codes)->pluck('version')->map(fn ($v) => (int) $v)->sort()->values()->all())->toBe($versions);
});
