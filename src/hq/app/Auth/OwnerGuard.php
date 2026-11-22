<?php

namespace App\Auth;

use Closure;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\ValidationException;

/**
 * The system must never be left without an active OWNER: a user that is active, not deleted, and holds the role with
 * code OWNER for all nodes (user_roles.node_id NULL). A change that could remove the last one runs through protect():
 * the OWNER role row is locked (so two admins cannot each remove "the other" owner at the same time), the change runs,
 * and when owners existed before but none remain, everything is rolled back with a message.
 */
final class OwnerGuard
{
    public const ROLE_CODE = 'OWNER';

    /**
     * @template T
     *
     * @param  Closure(): T  $change
     * @return T
     *
     * @throws ValidationException the change would leave no active OWNER
     */
    public function protect(Closure $change, string $errorKey = 'owner'): mixed
    {
        return DB::transaction(function () use ($change, $errorKey) {
            DB::table('roles')->where('code', self::ROLE_CODE)->lockForUpdate()->value('id');
            $before = $this->activeOwners();
            $result = $change();
            if ($before > 0 && $this->activeOwners() === 0) {
                throw ValidationException::withMessages([
                    $errorKey => 'Harus selalu ada minimal satu pengguna OWNER aktif (untuk semua node). Perubahan dibatalkan.',
                ]);
            }

            return $result;
        });
    }

    public function activeOwners(): int
    {
        return DB::table('users as u')
            ->join('user_roles as ur', 'ur.user_id', '=', 'u.id')
            ->join('roles as r', 'r.id', '=', 'ur.role_id')
            ->where('r.code', self::ROLE_CODE)
            ->whereNull('r.deleted_at')
            ->whereNull('ur.deleted_at')
            ->whereNull('ur.node_id')
            ->where('u.is_active', true)
            ->whereNull('u.deleted_at')
            ->distinct()
            ->count('u.id');
    }
}
