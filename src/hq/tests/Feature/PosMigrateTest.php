<?php

use App\Support\Schema\SqlMigrator;
use Illuminate\Support\Facades\Artisan;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\File;
use Illuminate\Support\Facades\Schema;
use Tests\TestCase;

function sharedScripts(): array
{
    $files = array_map('basename', glob(config('pos.migrations_path').'/*.sql'));
    sort($files, SORT_STRING);

    return $files;
}

function journal(): array
{
    return array_map(fn ($r) => $r->scriptname, DB::select('SELECT scriptname FROM schema_versions ORDER BY schemaversionid'));
}

it('applies every shared SQL script once, in name order, journaled by file name', function () {
    expect(journal())->toBe(sharedScripts())
        ->and(Schema::hasTable('users'))->toBeTrue()
        ->and(Schema::hasTable('master_version'))->toBeTrue()
        ->and(Schema::hasTable('sale_returns'))->toBeTrue();
});

it('is idempotent: a second run applies nothing and adds no journal rows', function () {
    $before = DB::table('schema_versions')->count();

    expect(Artisan::call('pos:migrate'))->toBe(0)
        ->and(Artisan::output())->toContain('up to date')
        ->and(DB::table('schema_versions')->count())->toBe($before);
});

it('has the same journal table as DbUp at the nodes', function () {
    $columns = DB::select(
        "SELECT column_name AS name, column_type AS type, is_nullable AS nullable, extra AS extra
         FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'schema_versions'
         ORDER BY ordinal_position"
    );

    expect(array_map(fn ($c) => [$c->name, $c->type, $c->nullable], $columns))->toBe([
        ['schemaversionid', 'int', 'NO'],
        ['scriptname', 'varchar(255)', 'NO'],
        ['applied', 'timestamp', 'NO'],
    ])->and($columns[0]->extra)->toContain('auto_increment');

    // A database migrated by the Node Service (DbUp) on this machine, when there is one: identical columns.
    $dbUp = DB::select(
        "SELECT column_name AS name, column_type AS type, is_nullable AS nullable
         FROM information_schema.columns WHERE table_schema = 'pos_test' AND table_name = 'schema_versions'
         ORDER BY ordinal_position"
    );
    if ($dbUp !== []) {
        expect(array_map(fn ($c) => [$c->name, $c->type, $c->nullable], $dbUp))
            ->toBe(array_map(fn ($c) => [$c->name, $c->type, $c->nullable], $columns));
        expect(DB::selectOne('SELECT GROUP_CONCAT(scriptname ORDER BY schemaversionid) AS s FROM pos_test.schema_versions')->s)
            ->toStartWith('0001_initial_schema.sql,0002_login_throttle.sql');
    }
});

it('continues a database that the other side migrated part of, without running a script twice', function () {
    // The node (DbUp) migrated up to 0005: same tables, same journal rows.
    $partial = storage_path('framework/testing/partial-migrations');
    File::deleteDirectory($partial);
    File::ensureDirectoryExists($partial);
    foreach (array_slice(sharedScripts(), 0, 5) as $name) {
        File::copy(config('pos.migrations_path')."/{$name}", "{$partial}/{$name}");
    }

    try {
        Schema::dropAllTables();
        $first = (new SqlMigrator(DB::connection(), $partial))->migrate();
        DB::update('UPDATE schema_versions SET applied = UTC_TIMESTAMP() - INTERVAL 1 DAY');   // "earlier, by DbUp"

        $rest = (new SqlMigrator(DB::connection(), config('pos.migrations_path')))->migrate();

        expect($first)->toBe(array_slice(sharedScripts(), 0, 5))
            ->and($rest)->toBe(array_slice(sharedScripts(), 5))
            ->and(journal())->toBe(sharedScripts())
            ->and(DB::selectOne('SELECT COUNT(DISTINCT scriptname) AS c FROM schema_versions')->c)->toBe(count(sharedScripts()));
    } finally {
        File::deleteDirectory($partial);
        TestCase::rebuildDatabase();
    }
});

it('stamps the journal in UTC', function () {
    $applied = DB::selectOne('SELECT MAX(applied) AS a FROM schema_versions')->a;
    $now = DB::selectOne('SELECT UTC_TIMESTAMP() AS n')->n;

    expect(abs(strtotime($now) - strtotime($applied)))->toBeLessThan(3600);
});

it('stops at a failing script and keeps the scripts before it', function () {
    $broken = storage_path('framework/testing/broken-migrations');
    File::deleteDirectory($broken);
    File::ensureDirectoryExists($broken);
    File::put("{$broken}/9001_ok.sql", 'CREATE TABLE zz_migrate_ok (id INT);');
    File::put("{$broken}/9002_broken.sql", 'CREATE TABLE zz_migrate_broken (id INT); THIS IS NOT SQL;');
    File::put("{$broken}/9003_never.sql", 'CREATE TABLE zz_migrate_never (id INT);');

    try {
        expect(fn () => (new SqlMigrator(DB::connection(), $broken))->migrate())
            ->toThrow(RuntimeException::class, '9002_broken.sql');
        expect(journal())->toContain('9001_ok.sql')
            ->not->toContain('9002_broken.sql')
            ->not->toContain('9003_never.sql')
            ->and(Schema::hasTable('zz_migrate_never'))->toBeFalse();
    } finally {
        File::deleteDirectory($broken);
        DB::delete("DELETE FROM schema_versions WHERE scriptname LIKE '900%'");
        Schema::dropIfExists('zz_migrate_ok');
        Schema::dropIfExists('zz_migrate_broken');
    }
});

it('never touches domain tables from a Laravel migration', function () {
    $domain = [];
    foreach (glob(config('pos.migrations_path').'/*.sql') as $file) {
        preg_match_all('/CREATE TABLE\s+`?(\w+)`?/i', (string) file_get_contents($file), $m);
        $domain = array_merge($domain, $m[1]);
    }
    expect($domain)->toContain('users');

    foreach (glob(database_path('migrations/*.php')) as $file) {
        preg_match_all("/Schema::(?:create|table|drop|dropIfExists|rename)\(\s*'(\w+)'/", (string) file_get_contents($file), $m);
        expect(array_intersect($m[1], $domain))->toBe([], basename($file).' touches a domain table');
    }
});
