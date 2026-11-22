<?php

namespace App\Auth;

use Illuminate\Support\Facades\DB;

/**
 * A user's permissions at HQ: roles from user_roles that are not deleted, for all nodes (node_id NULL) or for the
 * HQ node (config pos.hq_node_code, default "HQ") — the same query as the node uses for its own node.
 */
final class UserPermissions
{
    /** Required to enter the HQ web at all. */
    public const HQ_ACCESS = 'hq.access';

    public function for(string $userId): PermissionSet
    {
        $json = DB::table('user_roles as ur')
            ->join('roles as r', 'r.id', '=', 'ur.role_id')
            ->leftJoin('nodes as n', 'n.id', '=', 'ur.node_id')
            ->where('ur.user_id', $userId)
            ->whereNull('ur.deleted_at')
            ->whereNull('r.deleted_at')
            ->where(fn ($q) => $q->whereNull('ur.node_id')->orWhere('n.code', config('pos.hq_node_code')))
            ->selectRaw('CAST(r.permissions AS CHAR) AS permissions')
            ->pluck('permissions')
            ->map(fn (mixed $value) => is_string($value) ? $value : null);

        return PermissionSet::fromRoleJson($json);
    }
}
