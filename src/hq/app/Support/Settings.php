<?php

namespace App\Support;

use Illuminate\Support\Facades\DB;

/** Global settings rows (settings.branch_id NULL), JSON values — the same table the nodes read. */
final class Settings
{
    /** pricing.uniform = true: one price for all branches; branch prices are hidden here and ignored by the nodes. */
    public const PRICING_UNIFORM = 'pricing.uniform';

    public static function json(string $key): mixed
    {
        $value = DB::table('settings')->whereNull('branch_id')->where('setting_key', $key)->whereNull('deleted_at')->value('setting_value');

        return is_string($value) ? json_decode($value, true) : null;
    }

    /** Missing = false (prices may differ per branch, CLAUDE.md). */
    public static function pricingUniform(): bool
    {
        $value = self::json(self::PRICING_UNIFORM);

        return $value === true || $value === 1 || $value === '1' || $value === 'true';
    }
}
