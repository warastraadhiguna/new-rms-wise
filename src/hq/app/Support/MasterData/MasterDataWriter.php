<?php

namespace App\Support\MasterData;

use Carbon\CarbonImmutable;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Schema;
use Illuminate\Support\Str;
use InvalidArgumentException;
use RuntimeException;

/**
 * The ONLY way HQ writes master data (rule 1 and 4 of CLAUDE.md). One transaction per write:
 *  - master_version.last_version + 1 under SELECT ... FOR UPDATE (one global sequence; nodes pull "version > N"),
 *  - the row gets that version (create, update, and soft delete alike),
 *  - new rows get a UUID v7 id, deletes are soft (deleted_at + new version), never DELETE,
 *  - audit_logs records user, table, id, action, old and new values, IP.
 * Two parallel writes therefore always get different versions.
 */
final class MasterDataWriter
{
    public const ACTION_CREATE = 'CREATE';

    public const ACTION_UPDATE = 'UPDATE';

    public const ACTION_DELETE = 'DELETE';

    public const ACTION_RESTORE = 'RESTORE';

    /** Columns the writer owns; callers cannot set them. */
    private const OWNED = ['id', 'version', 'deleted_at'];

    /** @var array<string, list<string>> */
    private array $columns = [];

    /**
     * @param  array<string, mixed>  $values  column => value (no id, version or deleted_at)
     * @return string the new id (UUID v7)
     */
    public function create(string $table, array $values, ?WriteContext $context = null): string
    {
        $this->guard($table, $values);
        $id = Str::uuid7()->toString();

        DB::transaction(function () use ($table, $values, $id, $context) {
            $version = $this->nextVersion();
            $row = ['id' => $id] + $values + ['version' => $version];
            DB::table($table)->insert($row);
            $this->audit($table, $id, self::ACTION_CREATE, null, $this->load($table, $id), $context);
        });

        return $id;
    }

    /**
     * @param  array<string, mixed>  $values  changed columns (no id, version or deleted_at)
     *
     * @throws RuntimeException the row does not exist or is deleted
     */
    public function update(string $table, string $id, array $values, ?WriteContext $context = null): int
    {
        $this->guard($table, $values);

        return DB::transaction(function () use ($table, $id, $values, $context) {
            $old = $this->lockActive($table, $id);
            $version = $this->nextVersion();
            DB::table($table)->where('id', $id)->update($values + ['version' => $version]);
            $this->audit($table, $id, self::ACTION_UPDATE, $old, $this->load($table, $id), $context);

            return $version;
        });
    }

    /**
     * Soft delete: deleted_at = now (UTC) and a new version, so nodes receive the deletion with their next pull.
     *
     * @throws RuntimeException the row does not exist or is already deleted
     */
    public function delete(string $table, string $id, ?WriteContext $context = null): int
    {
        $this->guard($table, []);

        return DB::transaction(function () use ($table, $id, $context) {
            $old = $this->lockActive($table, $id);
            $version = $this->nextVersion();
            DB::table($table)->where('id', $id)->update([
                'deleted_at' => CarbonImmutable::now('UTC')->format('Y-m-d H:i:s.v'),
                'version' => $version,
            ]);
            $this->audit($table, $id, self::ACTION_DELETE, $old, $this->load($table, $id), $context);

            return $version;
        });
    }

    /**
     * Brings a soft-deleted row back (deleted_at NULL, new version), optionally with changed columns — e.g. a barcode
     * that was removed and is now given to another unit (the unique key still holds the deleted row).
     *
     * @param  array<string, mixed>  $values
     *
     * @throws RuntimeException the row does not exist or is not deleted
     */
    public function restore(string $table, string $id, array $values = [], ?WriteContext $context = null): int
    {
        $this->guard($table, $values);

        return DB::transaction(function () use ($table, $id, $values, $context) {
            $row = DB::table($table)->where('id', $id)->whereNotNull('deleted_at')->lockForUpdate()->first();
            if ($row === null) {
                throw new RuntimeException("{$table} {$id} does not exist or is not deleted.");
            }
            $version = $this->nextVersion();
            DB::table($table)->where('id', $id)->update($values + ['deleted_at' => null, 'version' => $version]);
            $this->audit($table, $id, self::ACTION_RESTORE, (array) $row, $this->load($table, $id), $context);

            return $version;
        });
    }

    /** The global master data version (what the newest row carries). */
    public function currentVersion(): int
    {
        return (int) (DB::table('master_version')->where('id', 1)->value('last_version') ?? 0);
    }

    /**
     * Lock the row first: migration 0001 inserts it. An INSERT IGNORE before the lock would take a shared lock on the
     * existing row, and two writers both holding it while asking FOR UPDATE deadlock (seen in the parallel test).
     */
    private function nextVersion(): int
    {
        $row = DB::selectOne('SELECT last_version FROM master_version WHERE id = 1 FOR UPDATE');
        if ($row === null) {
            DB::statement('INSERT IGNORE INTO master_version (id, last_version) VALUES (1, 0)');
            $row = DB::selectOne('SELECT last_version FROM master_version WHERE id = 1 FOR UPDATE');
        }
        $current = (int) $row->last_version;
        DB::update('UPDATE master_version SET last_version = ? WHERE id = 1', [$current + 1]);

        return $current + 1;
    }

    /** @return array<string, mixed> */
    private function lockActive(string $table, string $id): array
    {
        $row = DB::table($table)->where('id', $id)->whereNull('deleted_at')->lockForUpdate()->first();
        if ($row === null) {
            throw new RuntimeException("{$table} {$id} does not exist or is deleted.");
        }

        return (array) $row;
    }

    /** @return array<string, mixed> */
    private function load(string $table, string $id): array
    {
        return (array) DB::table($table)->where('id', $id)->first();
    }

    /**
     * @param  array<string, mixed>|null  $old
     * @param  array<string, mixed>  $new
     */
    private function audit(string $table, string $id, string $action, ?array $old, array $new, ?WriteContext $context): void
    {
        $context ??= WriteContext::fromRequest();
        DB::table('audit_logs')->insert([
            'user_id' => $context->userId,
            'table_name' => $table,
            'record_id' => $id,
            'action' => $action,
            'old_values' => $old === null ? null : json_encode($old, JSON_THROW_ON_ERROR | JSON_UNESCAPED_UNICODE),
            'new_values' => json_encode($new, JSON_THROW_ON_ERROR | JSON_UNESCAPED_UNICODE),
            'ip_address' => $context->ipAddress,
        ]);
    }

    /**
     * A master data table has id CHAR(36), version and deleted_at; the caller never sets those.
     *
     * @param  array<string, mixed>  $values
     */
    private function guard(string $table, array $values): void
    {
        if (! preg_match('/^[a-z_][a-z0-9_]*$/', $table)) {
            throw new InvalidArgumentException("Invalid table name: {$table}");
        }
        $columns = $this->columns[$table] ??= Schema::getColumnListing($table);
        foreach (self::OWNED as $owned) {
            if (! in_array($owned, $columns, true)) {
                throw new InvalidArgumentException("{$table} is not a master data table (no {$owned} column).");
            }
        }
        foreach (array_keys($values) as $column) {
            if (in_array($column, self::OWNED, true)) {
                throw new InvalidArgumentException("Column {$column} is set by the master data writer.");
            }
            if (! in_array($column, $columns, true)) {
                throw new InvalidArgumentException("Unknown column {$table}.{$column}.");
            }
        }
    }
}
