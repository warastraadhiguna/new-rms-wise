<?php

namespace App\Sync;

/** The node a sync request acts for (resolved from its token). */
final readonly class SyncNode
{
    public function __construct(
        public string $id,
        public string $code,
        public string $branchId,
        public int $tokenId,
    ) {}
}
