<?php

use App\Support\Monitoring\NodeHealth;
use Carbon\CarbonImmutable;

/* Dashboard colours: green within sync.alert_minutes, yellow up to twice that (or refused documents / clock off), red after. */

it('colours a node by when it was last seen', function (?int $minutesAgo, int $alert, string $expected) {
    $now = CarbonImmutable::parse('2026-11-23 10:00:00', 'UTC');
    $seen = $minutesAgo === null ? null : $now->subMinutes($minutesAgo);

    expect(NodeHealth::of($seen, $now, $alert, 0, 0)['status'])->toBe($expected);
})->with([
    'never seen' => [null, 15, NodeHealth::RED],
    'just now' => [0, 15, NodeHealth::GREEN],
    'at the limit' => [15, 15, NodeHealth::GREEN],
    'past the limit' => [16, 15, NodeHealth::YELLOW],
    'twice the limit' => [30, 15, NodeHealth::YELLOW],
    'past twice the limit' => [31, 15, NodeHealth::RED],
    'longer limit' => [40, 60, NodeHealth::GREEN],
    'limit at least one minute' => [3, 0, NodeHealth::RED],
]);

it('turns yellow for refused documents or a clock that is off, with the reasons', function () {
    $now = CarbonImmutable::parse('2026-11-23 10:00:00', 'UTC');

    $failed = NodeHealth::of($now, $now, 15, 2, 0);
    expect($failed['status'])->toBe(NodeHealth::YELLOW)->and($failed['reasons'][0])->toContain('2 dokumen ditolak');

    $clock = NodeHealth::of($now, $now, 15, 0, -6 * 60 * 1000);
    expect($clock['status'])->toBe(NodeHealth::YELLOW)->and($clock['reasons'][0])->toContain('6 menit');

    expect(NodeHealth::of($now, $now, 15, 0, 4 * 60 * 1000))->toBe(['status' => NodeHealth::GREEN, 'reasons' => []])
        ->and(NodeHealth::of(null, $now, 15, 0, null)['reasons'])->toBe(['Belum pernah tersambung ke pusat.']);
});
