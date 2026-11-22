<?php

namespace App\Support\Schema;

use Illuminate\Database\Connection;

/**
 * Every table must be InnoDB (transactions, row locks, foreign keys) — the same check as the Node Service at start.
 * WAMP's default engine is MyISAM, so a table created without ENGINE=InnoDB would silently lose those guarantees.
 */
final class StorageEngineCheck
{
    /** @return list<string> "table (ENGINE)" for every base table of the current database that is not InnoDB */
    public static function nonInnoDbTables(Connection $db): array
    {
        $rows = $db->table('information_schema.tables')
            ->whereRaw('table_schema = DATABASE()')
            ->where('table_type', 'BASE TABLE')
            ->where(fn ($q) => $q->whereNull('engine')->orWhere('engine', '<>', 'InnoDB'))
            ->orderBy('table_name')
            ->selectRaw('table_name AS name, engine AS engine')
            ->get();

        $tables = [];
        foreach ($rows as $row) {
            $name = is_scalar($row->name ?? null) ? (string) $row->name : '?';
            $engine = is_scalar($row->engine ?? null) ? (string) $row->engine : 'unknown';
            $tables[] = "{$name} ({$engine})";
        }

        return $tables;
    }
}
