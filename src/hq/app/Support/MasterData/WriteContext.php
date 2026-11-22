<?php

namespace App\Support\MasterData;

/** Who made a master data change and from where (audit_logs.user_id and ip_address). */
final readonly class WriteContext
{
    public function __construct(public ?string $userId, public ?string $ipAddress) {}

    /** The logged-in user and the client IP of the current request (null / null on the console). */
    public static function fromRequest(): self
    {
        $request = app()->runningInConsole() && ! app()->runningUnitTests() ? null : request();

        return new self(auth()->id() === null ? null : (string) auth()->id(), $request?->ip());
    }

    public static function system(): self
    {
        return new self(null, null);
    }
}
