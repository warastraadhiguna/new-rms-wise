<?php

namespace App\Http\Middleware;

use App\Support\Schema\StorageEngineCheck;
use Closure;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Log;
use Symfony\Component\HttpFoundation\Response;

/**
 * The HQ web refuses to run on a database with a table that is not InnoDB (checked once per PHP process; a failed
 * check is repeated on the next request so the fix takes effect without a restart).
 */
class EnsureInnoDbSchema
{
    private static bool $verified = false;

    public function handle(Request $request, Closure $next): Response
    {
        if (! self::$verified) {
            $tables = StorageEngineCheck::nonInnoDbTables(DB::connection());
            if ($tables !== []) {
                Log::critical('Tables that are not InnoDB: '.implode(', ', $tables).'. Convert them with ALTER TABLE ... ENGINE=InnoDB.');

                return response('Database belum siap: ada tabel yang bukan InnoDB ('.implode(', ', $tables).'). Hubungi admin.', 503)
                    ->header('Content-Type', 'text/plain; charset=UTF-8');
            }
            self::$verified = true;
        }

        return $next($request);
    }

    /** Tests: check again on the next request. */
    public static function reset(): void
    {
        self::$verified = false;
    }
}
