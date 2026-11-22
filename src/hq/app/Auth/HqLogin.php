<?php

namespace App\Auth;

use App\Models\User;
use Carbon\CarbonImmutable;
use Illuminate\Support\Facades\DB;

/**
 * Login with username + password against the shared users table, like AuthService at the node: the attempt is
 * counted per login name under a row lock (login_throttle), unknown / inactive / deleted users and wrong passwords
 * give the same answer and always cost one bcrypt verification, 5 failures lock the name for 5 minutes. A correct
 * password without permission hq.access is not a failure but does not log in.
 */
final class HqLogin
{
    private static ?string $timingDummyHash = null;

    public function __construct(private readonly UserPermissions $permissions) {}

    public function attempt(string $username, string $password): LoginOutcome
    {
        $loginName = mb_strtolower(trim($username));
        $now = CarbonImmutable::now('UTC');

        return DB::transaction(function () use ($loginName, $password, $now) {
            DB::statement(
                'INSERT INTO login_throttle (login_name) VALUES (?) ON DUPLICATE KEY UPDATE login_name = login_name',
                [mb_substr($loginName, 0, 50)],
            );
            $row = DB::selectOne(
                'SELECT failed_count, locked_until FROM login_throttle WHERE login_name = ? FOR UPDATE',
                [mb_substr($loginName, 0, 50)],
            );
            $state = [
                'failed_count' => (int) $row->failed_count,
                'locked_until' => $row->locked_until === null ? null : CarbonImmutable::parse($row->locked_until, 'UTC'),
            ];
            if (LoginThrottle::isLocked($state, $now)) {
                return LoginOutcome::locked(LoginThrottle::remainingSeconds($state, $now));
            }

            /** @var User|null $user */
            $user = $loginName === '' ? null : User::query()
                ->where('username', $loginName)
                ->where('is_active', true)
                ->whereNull('deleted_at')
                ->first();

            // Always one bcrypt verification, so an unknown user takes as long as a wrong password.
            $verified = password_verify($password, $user !== null ? $user->password_hash : self::timingDummyHash()) && $user !== null;

            if (! $verified) {
                $this->save($loginName, LoginThrottle::onFailure($state, $now), $now);

                return LoginOutcome::invalid();
            }

            $this->save($loginName, ['failed_count' => 0, 'locked_until' => null], null);

            return $this->permissions->for($user->id)->has(UserPermissions::HQ_ACCESS)
                ? LoginOutcome::success($user)
                : LoginOutcome::noAccess();
        });
    }

    /** @param array{failed_count: int, locked_until: ?CarbonImmutable} $state */
    private function save(string $loginName, array $state, ?CarbonImmutable $failedAt): void
    {
        DB::update(
            <<<'SQL'
            UPDATE login_throttle
            SET failed_count = ?, locked_until = ?, last_failed_at = COALESCE(?, last_failed_at)
            WHERE login_name = ?
            SQL,
            [
                $state['failed_count'],
                $state['locked_until']?->format('Y-m-d H:i:s.v'),
                $failedAt?->format('Y-m-d H:i:s.v'),
                mb_substr($loginName, 0, 50),
            ],
        );
    }

    private static function timingDummyHash(): string
    {
        return self::$timingDummyHash ??= password_hash('timing-dummy-'.bin2hex(random_bytes(8)), PASSWORD_BCRYPT, ['cost' => 12]);
    }
}
