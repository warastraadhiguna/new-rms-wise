<?php

namespace App\Sync;

use Illuminate\Database\QueryException;
use Illuminate\Support\Facades\DB;
use Throwable;

/**
 * POST /sync/push: stores node documents as copies in HQ's tables (same schema) and records inbound_documents.
 * Each document is checked and applied in its own transaction, so one bad document never affects the others.
 * Idempotent per doc_id: the same row_version again = duplicate, a lower one = stale (ignored), a higher one
 * (a mutable document such as a closed shift) overwrites the copy.
 */
final class DocumentPush
{
    private const UUID = '/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/';

    private const DATETIME = '/^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(\.\d{1,6})?Z?$/';

    public function __construct(private readonly SyncSchema $schema) {}

    /**
     * @param  list<mixed>  $documents
     * @return list<array<string, mixed>>
     */
    public function push(SyncNode $node, array $documents): array
    {
        $results = [];
        foreach ($documents as $document) {
            $results[] = $this->one($node, is_array($document) ? $document : []);
        }
        DB::table('node_heartbeats')->where('node_id', $node->id)->update(['last_push_at' => NodeTokens::now()]);

        return $results;
    }

    /**
     * @param  array<string, mixed>  $doc
     * @return array<string, mixed>
     */
    private function one(SyncNode $node, array $doc): array
    {
        $result = ['outbox_id' => $doc['outbox_id'] ?? null, 'doc_id' => $doc['doc_id'] ?? null];
        try {
            $checked = $this->check($node, $doc);
        } catch (SyncRejection $e) {
            return $result + $e->toArray();
        }

        for ($attempt = 1; ; $attempt++) {
            try {
                return $result + ['status' => DB::transaction(fn () => $this->apply($node, $checked))];
            } catch (SyncRejection $e) {
                return $result + $e->toArray();
            } catch (QueryException $e) {
                $code = (int) ($e->errorInfo[1] ?? 0);
                $message = (string) ($e->errorInfo[2] ?? $e->getMessage());
                if ($code === 1062 && str_contains($message, 'inbound_documents.PRIMARY') && $attempt === 1) {
                    continue;                          // a parallel push stored it first: evaluate again
                }

                return $result + match (true) {
                    $code === 1452 => (new SyncRejection('missing_reference', 'Data induk belum ada di pusat: '.$message, true))->toArray(),
                    $code === 1062 && str_contains($message, 'uk_inbound_node_outbox') => (new SyncRejection('outbox_id_reused', 'outbox_id sudah dipakai dokumen lain dari node ini.', false))->toArray(),
                    $code === 1062 => (new SyncRejection('conflict', 'Bentrok dengan data yang sudah ada: '.$message, false))->toArray(),
                    default => (new SyncRejection('database_error', $message, true))->toArray(),
                };
            } catch (Throwable $e) {
                report($e);

                return $result + (new SyncRejection('server_error', 'Kesalahan di pusat.', true))->toArray();
            }
        }
    }

    /**
     * Structure and ownership, before touching the database.
     *
     * @param  array<string, mixed>  $doc
     * @return array{doc_type: string, doc_id: string, row_version: int, outbox_id: int, occurred_at: string, app_version: ?string,
     *     tables: array<string, list<array<string, mixed>>>}
     */
    private function check(SyncNode $node, array $doc): array
    {
        $docType = is_string($doc['doc_type'] ?? null) ? $doc['doc_type'] : '';
        $spec = DocumentTypes::spec($docType);
        if ($spec === null) {
            throw new SyncRejection('unknown_doc_type', "Jenis dokumen tidak dikenal: {$docType}.");
        }
        $docId = is_string($doc['doc_id'] ?? null) ? strtolower($doc['doc_id']) : '';
        $rowVersion = $doc['row_version'] ?? null;
        $outboxId = $doc['outbox_id'] ?? null;
        $occurredAt = is_string($doc['occurred_at'] ?? null) ? $doc['occurred_at'] : '';
        $appVersion = $doc['app_version'] ?? null;
        $payload = $doc['payload'] ?? null;
        if (! preg_match(self::UUID, $docId) || ! is_int($rowVersion) || $rowVersion < 1 || ! is_int($outboxId) || $outboxId < 1
            || ! preg_match(self::DATETIME, $occurredAt) || ($appVersion !== null && (! is_string($appVersion) || strlen($appVersion) > 20))
            || ! is_array($payload) || ! is_array($payload['tables'] ?? null)) {
            throw new SyncRejection('invalid_document', 'Dokumen tidak lengkap (doc_id, row_version, outbox_id, occurred_at, payload.tables).');
        }
        if (($payload['node_id'] ?? null) !== $node->id) {
            throw new SyncRejection('foreign_node', 'Dokumen milik node lain.');
        }

        $tables = [];
        $count = 0;
        foreach ($payload['tables'] as $entry) {
            $table = is_array($entry) && is_string($entry['table'] ?? null) ? $entry['table'] : '';
            if (! array_key_exists($table, $spec['tables'])) {
                throw new SyncRejection('unknown_table', "Tabel {$table} tidak boleh ada di dokumen {$docType}.");
            }
            if (! is_array($entry['rows'] ?? null) || isset($tables[$table])) {
                throw new SyncRejection('invalid_document', "Tabel {$table}: rows tidak valid atau ganda.");
            }
            foreach ($entry['rows'] as $row) {
                if (! is_array($row) || array_is_list($row)) {
                    throw new SyncRejection('invalid_document', "Tabel {$table}: baris harus berupa objek.");
                }
                $tables[$table][] = $row;
                $count++;
            }
        }
        if ($count > (int) config('pos.sync.push_max_rows_per_document')) {
            throw new SyncRejection('document_too_large', 'Dokumen terlalu besar.');
        }

        $rootTable = (string) DocumentTypes::root($docType);
        $roots = $tables[$rootTable] ?? [];
        if (count($roots) !== 1 || strtolower((string) ($roots[0]['id'] ?? '')) !== $docId) {
            throw new SyncRejection('invalid_document', "Dokumen harus berisi tepat satu baris {$rootTable} dengan id = doc_id.");
        }
        $root = $roots[0];

        foreach ($tables as $table => $rows) {
            $columns = $this->schema->columns($table);
            $link = $spec['tables'][$table];
            foreach ($rows as $row) {
                foreach ($row as $column => $value) {
                    if (! in_array($column, $columns, true)) {
                        throw new SyncRejection('unknown_column', "Kolom {$table}.{$column} tidak dikenal.");
                    }
                    if ($value !== null && ! is_scalar($value)) {
                        throw new SyncRejection('invalid_document', "Kolom {$table}.{$column}: nilai harus teks, angka, atau null.");
                    }
                }
                if (! preg_match(self::UUID, strtolower((string) ($row['id'] ?? '')))) {
                    throw new SyncRejection('invalid_document', "Baris {$table} tanpa id yang valid.");
                }
                if (in_array('node_id', $columns, true) && ($row['node_id'] ?? null) !== $node->id) {
                    throw new SyncRejection('foreign_node', "Baris {$table} milik node lain.");
                }
                $belongs = match (true) {
                    $link === DocumentTypes::ROOT => true,
                    $link === DocumentTypes::DOC => strtolower((string) ($row['doc_id'] ?? '')) === $docId,
                    $link === DocumentTypes::DEVICE => ($row['id'] ?? null) === ($root['device_id'] ?? false),
                    default => strtolower((string) ($row[substr($link, 7)] ?? '')) === $docId,
                };
                if (! $belongs) {
                    throw new SyncRejection('foreign_row', "Baris {$table} bukan bagian dari dokumen ini.");
                }
            }
        }

        // In the spec's order (foreign keys).
        $ordered = [];
        foreach (array_keys($spec['tables']) as $table) {
            if (isset($tables[$table])) {
                $ordered[$table] = $tables[$table];
            }
        }

        return [
            'doc_type' => $docType, 'doc_id' => $docId, 'row_version' => $rowVersion, 'outbox_id' => $outboxId,
            'occurred_at' => substr(str_replace('T', ' ', rtrim($occurredAt, 'Z')), 0, 23), 'app_version' => $appVersion,
            'tables' => $ordered,
        ];
    }

    /**
     * Inside the document's transaction.
     *
     * @param  array{doc_type: string, doc_id: string, row_version: int, outbox_id: int, occurred_at: string, app_version: ?string,
     *     tables: array<string, list<array<string, mixed>>>}  $doc
     */
    private function apply(SyncNode $node, array $doc): string
    {
        $inbound = DB::table('inbound_documents')->where('doc_id', $doc['doc_id'])->lockForUpdate()->first();
        if ($inbound !== null) {
            if ($inbound->node_id !== $node->id || $inbound->doc_type !== $doc['doc_type']) {
                throw new SyncRejection('foreign_row', 'doc_id ini sudah dipakai dokumen lain.');
            }
            if ((int) $inbound->row_version === $doc['row_version']) {
                return 'duplicate';
            }
            if ((int) $inbound->row_version > $doc['row_version']) {
                return 'stale';
            }
        }

        $spec = DocumentTypes::TYPES[$doc['doc_type']];
        foreach ($doc['tables'] as $table => $rows) {
            $this->guardExisting($node, $doc['doc_id'], $table, (string) $spec['tables'][$table], $rows);
            foreach ($rows as $row) {
                DB::table($table)->upsert([$row], ['id'], array_values(array_diff(array_keys($row), ['id'])));
            }
        }

        $values = [
            'doc_type' => $doc['doc_type'], 'node_id' => $node->id, 'row_version' => $doc['row_version'],
            'outbox_id' => $doc['outbox_id'], 'app_version' => $doc['app_version'], 'occurred_at' => $doc['occurred_at'],
            'received_at' => NodeTokens::now(),
        ];
        $inbound === null
            ? DB::table('inbound_documents')->insert(['doc_id' => $doc['doc_id']] + $values)
            : DB::table('inbound_documents')->where('doc_id', $doc['doc_id'])->update($values);

        return 'accepted';
    }

    /**
     * A row may not overwrite a row of another node or of another document (ids are UUIDs, but never trust them).
     *
     * @param  list<array<string, mixed>>  $rows
     */
    private function guardExisting(SyncNode $node, string $docId, string $table, string $link, array $rows): void
    {
        $columns = $this->schema->columns($table);
        $owner = match (true) {
            $link === DocumentTypes::DOC => 'doc_id',
            str_starts_with($link, 'parent:') => substr($link, 7),
            default => null,
        };
        $select = array_values(array_filter(['id', in_array('node_id', $columns, true) ? 'node_id' : null, $owner]));
        $existing = DB::table($table)->whereIn('id', array_map(fn (array $r) => $r['id'], $rows))->lockForUpdate()->get($select);
        foreach ($existing as $row) {
            if ((isset($row->node_id) && $row->node_id !== $node->id) || ($owner !== null && strtolower((string) $row->{$owner}) !== $docId)) {
                throw new SyncRejection('foreign_row', "Baris {$table} {$row->id} milik dokumen atau node lain.");
            }
        }
    }
}
