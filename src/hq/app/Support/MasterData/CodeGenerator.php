<?php

namespace App\Support\MasterData;

use Illuminate\Database\QueryException;
use Illuminate\Support\Facades\DB;

/** Automatic codes such as C000124 / S000007 (prefix + zero-padded number), deleted rows included. */
final class CodeGenerator
{
    /** The next free code: highest existing "{prefix}{digits}" + 1. */
    public static function next(string $table, string $column, string $prefix, int $digits = 6): string
    {
        $pattern = '^'.preg_quote($prefix, '/').'[0-9]{'.$digits.'}$';
        $max = DB::table($table)->where($column, 'regexp', $pattern)->max($column);
        $number = is_string($max) ? (int) substr($max, strlen($prefix)) + 1 : 1;

        return $prefix.str_pad((string) $number, $digits, '0', STR_PAD_LEFT);
    }

    /**
     * Runs $create with a fresh code; when a parallel request took the same code (unique key), tries again.
     *
     * @template T
     *
     * @param  callable(string): T  $create
     * @return T
     */
    public static function withNext(string $table, string $column, string $prefix, callable $create, int $attempts = 3): mixed
    {
        for ($i = 1; ; $i++) {
            try {
                return $create(self::next($table, $column, $prefix));
            } catch (QueryException $e) {
                if ($i >= $attempts || ($e->errorInfo[1] ?? null) !== 1062) {
                    throw $e;
                }
            }
        }
    }
}
