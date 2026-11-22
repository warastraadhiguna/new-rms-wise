<?php

namespace App\Support\Monitoring;

use Carbon\CarbonImmutable;

/**
 * Colour of a node on the HQ dashboard from its last contact and heartbeat, with the limit of setting
 * sync.alert_minutes (the node's branch, else global, else 15 — the same setting turns the cashier's status bar red):
 *   green   seen within the limit, no refused documents, clock within 5 minutes of HQ
 *   yellow  seen within twice the limit, or within the limit but with refused documents or a clock that is off
 *   red     never seen, or not seen for more than twice the limit
 */
final class NodeHealth
{
    public const GREEN = 'green';

    public const YELLOW = 'yellow';

    public const RED = 'red';

    public const DEFAULT_ALERT_MINUTES = 15;

    public const MAX_CLOCK_SKEW_MS = 5 * 60 * 1000;

    /** @return array{status: string, reasons: list<string>} */
    public static function of(?CarbonImmutable $lastSeen, CarbonImmutable $now, int $alertMinutes, int $failedOutbox, ?int $clockSkewMs): array
    {
        $alertMinutes = max(1, $alertMinutes);
        if ($lastSeen === null) {
            return ['status' => self::RED, 'reasons' => ['Belum pernah tersambung ke pusat.']];
        }

        $minutes = max(0, $lastSeen->diffInSeconds($now, false) / 60);
        $reasons = [];
        if ($minutes > 2 * $alertMinutes) {
            return ['status' => self::RED, 'reasons' => ['Tidak tersambung lebih dari '.(2 * $alertMinutes).' menit.']];
        }
        $status = self::GREEN;
        if ($minutes > $alertMinutes) {
            $status = self::YELLOW;
            $reasons[] = "Tidak tersambung lebih dari {$alertMinutes} menit.";
        }
        if ($failedOutbox > 0) {
            $status = self::YELLOW;
            $reasons[] = "{$failedOutbox} dokumen ditolak pusat dan perlu diperiksa.";
        }
        if ($clockSkewMs !== null && abs($clockSkewMs) > self::MAX_CLOCK_SKEW_MS) {
            $status = self::YELLOW;
            $reasons[] = 'Jam komputer node berbeda '.(int) round(abs($clockSkewMs) / 60000).' menit dari pusat.';
        }

        return ['status' => $status, 'reasons' => $reasons];
    }
}
