<?php

use App\Http\Middleware\EnsureInnoDbSchema;
use App\Support\Schema\SqlSplitter;
use App\Support\Schema\StorageEngineCheck;
use Illuminate\Support\Facades\Artisan;
use Illuminate\Support\Facades\DB;

it('has only InnoDB tables after pos:migrate and migrate, the DbUp journal included', function () {
    expect(StorageEngineCheck::nonInnoDbTables(DB::connection()))->toBe([])
        ->and(DB::selectOne("SELECT engine AS e FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'schema_versions'")->e)
        ->toBe('InnoDB');
});

it('refuses to serve and pos:migrate fails while a table is not InnoDB', function () {
    $table = 'zz_myisam_'.substr(md5(uniqid()), 0, 8);
    DB::statement("CREATE TABLE {$table} (id INT) ENGINE=MyISAM");
    EnsureInnoDbSchema::reset();

    try {
        expect(StorageEngineCheck::nonInnoDbTables(DB::connection()))->toBe(["{$table} (MyISAM)"]);
        $this->get('/login')->assertStatus(503)->assertSee($table);
        expect(Artisan::call('pos:migrate'))->toBe(1)
            ->and(Artisan::output())->toContain($table);
    } finally {
        DB::statement("DROP TABLE {$table}");
        EnsureInnoDbSchema::reset();
    }

    $this->get('/login')->assertOk();
});

it('declares ENGINE=InnoDB on every CREATE TABLE of the shared migrations', function () {
    $checked = 0;
    foreach (glob(config('pos.migrations_path').'/*.sql') as $file) {
        foreach (SqlSplitter::split((string) file_get_contents($file)) as $statement) {
            if (preg_match('/^CREATE TABLE\s+`?(\w+)/i', $statement, $m)) {
                expect($statement)->toMatch('/\)\s*ENGINE\s*=\s*InnoDB\b/i', basename($file).': '.$m[1]);
                $checked++;
            }
        }
    }
    expect($checked)->toBeGreaterThan(70);
});
