<?php

namespace App\Sync;

use Carbon\CarbonImmutable;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Str;

/**
 * Sync tokens: "pos_" + 48 random characters, shown once; only the SHA-256 hash is stored (node_sync_tokens).
 * A new token revokes the node's previous ones (one active token per node).
 */
final class NodeTokens
{
    public const PREFIX = 'pos_';

    /** @return string the plain token (shown once) */
    public function create(string $nodeId, ?string $userId): string
    {
        $token = self::PREFIX.Str::random(48);
        $now = self::now();

        DB::transaction(function () use ($nodeId, $userId, $token, $now) {
            DB::table('node_sync_tokens')->where('node_id', $nodeId)->whereNull('revoked_at')
                ->update(['revoked_at' => $now, 'revoked_by' => $userId]);
            DB::table('node_sync_tokens')->insert([
                'node_id' => $nodeId,
                'token_hash' => hash('sha256', $token),
                'token_hint' => '…'.substr($token, -6),
                'created_by' => $userId,
                'created_at' => $now,
            ]);
        });

        return $token;
    }

    public function revoke(string $nodeId, ?string $userId): int
    {
        return DB::table('node_sync_tokens')->where('node_id', $nodeId)->whereNull('revoked_at')
            ->update(['revoked_at' => self::now(), 'revoked_by' => $userId]);
    }

    /**
     * The not deleted node of a token that is not revoked, or null; "active" tells whether the node may sync.
     *
     * @return array{node: SyncNode, active: bool}|null
     */
    public function resolve(string $token): ?array
    {
        if (! str_starts_with($token, self::PREFIX) || strlen($token) > 200) {
            return null;
        }

        $row = DB::table('node_sync_tokens as t')
            ->join('nodes as n', 'n.id', '=', 't.node_id')
            ->where('t.token_hash', hash('sha256', $token))
            ->whereNull('t.revoked_at')
            ->whereNull('n.deleted_at')
            ->first(['t.id as token_id', 'n.id', 'n.code', 'n.branch_id', 'n.is_active']);
        if ($row === null) {
            return null;
        }
        $values = (array) $row;

        return [
            'node' => new SyncNode((string) $values['id'], (string) $values['code'], (string) $values['branch_id'], (int) $values['token_id']),
            'active' => (bool) $values['is_active'],
        ];
    }

    /** @return array{id: int, token_hint: string, created_at: string, last_used_at: ?string}|null */
    public function active(string $nodeId): ?array
    {
        $row = DB::table('node_sync_tokens')->where('node_id', $nodeId)->whereNull('revoked_at')
            ->orderByDesc('id')->first(['id', 'token_hint', 'created_at', 'last_used_at']);

        return $row === null ? null : [
            'id' => (int) $row->id,
            'token_hint' => (string) $row->token_hint,
            'created_at' => (string) $row->created_at,
            'last_used_at' => $row->last_used_at === null ? null : (string) $row->last_used_at,
        ];
    }

    public static function now(): string
    {
        return CarbonImmutable::now('UTC')->format('Y-m-d H:i:s.v');
    }
}
