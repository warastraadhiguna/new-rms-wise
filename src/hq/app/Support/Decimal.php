<?php

namespace App\Support;

use Brick\Math\BigDecimal;
use Brick\Math\RoundingMode;
use InvalidArgumentException;

/**
 * Money and quantities without float: DECIMAL(18,4) in MySQL, decimal strings ("12500.0000") in PHP and JSON,
 * arithmetic with brick/math. Models cast such columns with 'decimal:4' (a string, never a float).
 */
final class Decimal
{
    /** Same scale as DECIMAL(18,4) and the node's decimal strings. */
    public const SCALE = 4;

    /**
     * @throws InvalidArgumentException for floats (they are never exact) and malformed strings
     */
    public static function of(mixed $value): BigDecimal
    {
        if ($value instanceof BigDecimal) {
            return $value;
        }
        if (! is_string($value) && ! is_int($value)) {
            throw new InvalidArgumentException('Money and quantities are decimal strings or integers, never '.get_debug_type($value).'.');
        }
        if (is_string($value) && ! preg_match('/^-?\d+(\.\d+)?$/', trim($value))) {
            throw new InvalidArgumentException("Not a decimal number: '{$value}'.");
        }

        return BigDecimal::of(is_string($value) ? trim($value) : $value);
    }

    /**
     * "12500.0000": the storage and API form (rounded half away from zero to 4 decimals).
     *
     * @param  int<0, max>  $scale
     */
    public static function toString(BigDecimal|string|int $value, int $scale = self::SCALE): string
    {
        return (string) self::of($value)->toScale($scale, RoundingMode::HalfUp);
    }

    public static function add(BigDecimal|string|int ...$values): BigDecimal
    {
        $sum = BigDecimal::zero();
        foreach ($values as $value) {
            $sum = $sum->plus(self::of($value));
        }

        return $sum;
    }

    public static function compare(BigDecimal|string|int $a, BigDecimal|string|int $b): int
    {
        return self::of($a)->compareTo(self::of($b));
    }
}
