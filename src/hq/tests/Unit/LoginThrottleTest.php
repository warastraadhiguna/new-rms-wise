<?php

use App\Auth\LoginThrottle;
use Carbon\CarbonImmutable;

it('locks after 5 consecutive failures for 5 minutes, like the node', function () {
    $now = CarbonImmutable::parse('2026-11-22 08:00:00', 'UTC');
    $state = ['failed_count' => 0, 'locked_until' => null];
    foreach (range(1, 4) as $n) {
        $state = LoginThrottle::onFailure($state, $now);
        expect($state)->toBe(['failed_count' => $n, 'locked_until' => null]);
    }

    $state = LoginThrottle::onFailure($state, $now);

    expect($state['failed_count'])->toBe(0)
        ->and($state['locked_until']?->toDateTimeString())->toBe('2026-11-22 08:05:00')
        ->and(LoginThrottle::isLocked($state, $now->addMinutes(4)))->toBeTrue()
        ->and(LoginThrottle::remainingSeconds($state, $now->addMinutes(4)))->toBe(60)
        ->and(LoginThrottle::isLocked($state, $now->addMinutes(5)))->toBeFalse()
        ->and(LoginThrottle::onFailure($state, $now->addMinutes(6))['failed_count'])->toBe(1);   // an expired lock starts a new round
});
