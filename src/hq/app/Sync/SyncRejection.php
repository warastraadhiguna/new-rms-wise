<?php

namespace App\Sync;

use RuntimeException;

/** A document HQ does not store; retry = true when the same document may be accepted later. */
final class SyncRejection extends RuntimeException
{
    public function __construct(public readonly string $reason, string $message, public readonly bool $retry = false)
    {
        parent::__construct($message);
    }

    /** @return array{status: string, code: string, message: string, retry: bool} */
    public function toArray(): array
    {
        return ['status' => 'rejected', 'code' => $this->reason, 'message' => $this->getMessage(), 'retry' => $this->retry];
    }
}
