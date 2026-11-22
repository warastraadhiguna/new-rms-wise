<?php

use App\Support\Decimal;

it('keeps money as decimal strings with 4 decimals', function () {
    expect(Decimal::toString('12500'))->toBe('12500.0000')
        ->and(Decimal::toString(7))->toBe('7.0000')
        ->and(Decimal::toString('0.33335'))->toBe('0.3334')        // half away from zero
        ->and(Decimal::toString('-0.33335'))->toBe('-0.3334');
});

it('adds exactly where float would not', function () {
    expect(Decimal::toString(Decimal::add('0.1', '0.2')))->toBe('0.3000')
        ->and(Decimal::toString(Decimal::add('999999999999999.9999', '0.0001')))->toBe('1000000000000000.0000');
});

it('compares decimals', function () {
    expect(Decimal::compare('10.50', '10.5000'))->toBe(0)
        ->and(Decimal::compare('-1', '0'))->toBe(-1);
});

it('rejects floats and malformed numbers', function () {
    expect(fn () => Decimal::of(0.1))->toThrow(InvalidArgumentException::class)
        ->and(fn () => Decimal::of('1,5'))->toThrow(InvalidArgumentException::class)
        ->and(fn () => Decimal::of('1e3'))->toThrow(InvalidArgumentException::class);
});
