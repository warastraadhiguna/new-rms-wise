<?php

namespace App\Auth;

use App\Models\User;

/** Result of a login attempt at the HQ web. */
final readonly class LoginOutcome
{
    public const SUCCESS = 'success';

    public const INVALID = 'invalid';

    public const LOCKED = 'locked';

    public const NO_ACCESS = 'no_access';

    private function __construct(
        public string $status,
        public ?User $user = null,
        public int $lockedSeconds = 0,
    ) {}

    public static function success(User $user): self
    {
        return new self(self::SUCCESS, $user);
    }

    public static function invalid(): self
    {
        return new self(self::INVALID);
    }

    public static function locked(int $seconds): self
    {
        return new self(self::LOCKED, lockedSeconds: $seconds);
    }

    public static function noAccess(): self
    {
        return new self(self::NO_ACCESS);
    }
}
