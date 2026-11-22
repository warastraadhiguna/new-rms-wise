<?php

namespace App\Sync;

use Illuminate\Support\Facades\DB;

/**
 * GET /sync/pull: master data with version > since, in version order, deleted rows included, filtered for the node.
 *
 * Safe boundary: the committed master_version.last_version. MasterDataWriter takes a version under the
 * master_version row lock (SELECT ... FOR UPDATE) and keeps the lock until its transaction commits, so versions commit
 * in order and every version <= the committed last_version is visible. Rows above it (a write in flight, or one that
 * committed after the boundary was read) come in a later pull; nothing is skipped and the cursor is deterministic
 * (versions are unique).
 */
final class MasterPull
{
    public function __construct(private readonly SyncSchema $schema) {}

    /**
     * @return array{since: int, next_since: int, safe_version: int, has_more: bool,
     *     changes: list<array{table: string, version: int, row: array<string, mixed>}>}
     */
    public function pull(SyncNode $node, int $since, int $limit): array
    {
        $safe = (int) (DB::table('master_version')->where('id', 1)->value('last_version') ?? 0);

        $candidates = [];
        if ($safe > $since) {
            foreach (SyncSchema::MASTER_TABLES as $table) {
                $columns = $this->schema->columns($table);
                $rows = DB::table($table)
                    ->where('version', '>', $since)
                    ->where('version', '<=', $safe)
                    ->when(in_array($table, SyncSchema::BRANCH_FILTERED, true),
                        fn ($q) => $q->where(fn ($w) => $w->whereNull('branch_id')->orWhere('branch_id', $node->branchId)))
                    ->orderBy('version')
                    ->limit($limit + 1)                 // one more: tells whether anything is left
                    ->get($columns);
                foreach ($rows as $row) {
                    $values = (array) $row;
                    $candidates[] = ['table' => $table, 'version' => (int) $values['version'], 'row' => $values];
                }
            }
        }

        // The union of each table's first $limit + 1 rows holds the global first $limit + 1 rows.
        usort($candidates, fn (array $a, array $b) => $a['version'] <=> $b['version']);
        $hasMore = count($candidates) > $limit;
        $changes = array_slice($candidates, 0, $limit);
        $next = $hasMore ? $changes[count($changes) - 1]['version'] : max($since, $safe);

        return ['since' => $since, 'next_since' => $next, 'safe_version' => $safe, 'has_more' => $hasMore, 'changes' => $changes];
    }
}
