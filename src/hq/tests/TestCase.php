<?php

namespace Tests;

use Illuminate\Foundation\Testing\TestCase as BaseTestCase;
use Illuminate\Support\Facades\Artisan;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Schema;
use RuntimeException;

/**
 * Feature tests run against MySQL pos_hq_test (phpunit.xml), never the dev database. Once per test process the
 * database is rebuilt like a real HQ: all tables dropped, `pos:migrate` (domain schema from database/migrations/*.sql)
 * and `migrate` (framework tables only). Tests use their own unique data instead of transactions, so tests about
 * locking and parallel writes see real commits.
 */
abstract class TestCase extends BaseTestCase
{
    private static bool $prepared = false;

    protected function setUp(): void
    {
        parent::setUp();

        if (! self::$prepared) {
            self::rebuildDatabase();
            self::$prepared = true;
        }
    }

    /** Drops every table of the test database and builds it again from the migrations. */
    public static function rebuildDatabase(): void
    {
        $database = (string) DB::connection()->getDatabaseName();
        if (! str_ends_with($database, '_test')) {
            throw new RuntimeException("Refusing to rebuild '{$database}': tests only run against a *_test database.");
        }

        Schema::dropAllTables();
        if (Artisan::call('pos:migrate') !== 0) {
            throw new RuntimeException('pos:migrate failed: '.Artisan::output());
        }
        Artisan::call('migrate', ['--force' => true]);
    }
}
