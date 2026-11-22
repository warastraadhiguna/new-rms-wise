<?php

namespace App\Auth;

use Carbon\CarbonImmutable;

/**
 * Locks a login name for 5 minutes after 5 consecutive failed attempts — the same rule as
 * Pos.Shared.Auth.LoginThrottle at the node. Kept per login name (table login_throttle), also for names that do
 * not exist, so the behaviour never reveals whether the username or the password was wrong.
 */
final class LoginThrottle
{
    public const MAX_FAILURES = 5;

    public const LOCK_MINUTES = 5;

    /** @param array{failed_count: int, locked_until: ?CarbonImmutable} $state */
    public static function isLocked(array $state, CarbonImmutable $now): bool
    {
        return self::remainingSeconds($state, $now) > 0;
    }

    /** @param array{failed_count: int, locked_until: ?CarbonImmutable} $state */
    public static function remainingSeconds(array $state, CarbonImmutable $now): int
    {
        $until = $state['locked_until'];

        return $until !== null && $until->greaterThan($now) ? (int) ceil($now->diffInSeconds($until, true)) : 0;
    }

    /**
     * Call only when the attempt was not rejected for being locked.
     *
     * @param  array{failed_count: int, locked_until: ?CarbonImmutable}  $state
     * @return array{failed_count: int, locked_until: ?CarbonImmutable}
     */
    public static function onFailure(array $state, CarbonImmutable $now): array
    {
        $count = $state['locked_until'] !== null ? 1 : $state['failed_count'] + 1;   // an expired lock starts a new round

        return $count >= self::MAX_FAILURES
            ? ['failed_count' => 0, 'locked_until' => $now->addMinutes(self::LOCK_MINUTES)]
            : ['failed_count' => $count, 'locked_until' => null];
    }
}
