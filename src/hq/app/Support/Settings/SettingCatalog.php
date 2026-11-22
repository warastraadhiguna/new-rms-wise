<?php

namespace App\Support\Settings;

use App\Support\Decimal;
use App\Support\Printing\ReceiptFormatter;
use Illuminate\Validation\Rule;

/**
 * Every setting the HQ page edits: key (as read by the Node Service), group, Indonesian label, type, range, default,
 * and whether a branch may override it (settings.branch_id; the branch row wins at the node). Values are stored as
 * JSON the node understands: strings and enums as JSON strings, decimals as decimal strings ("100"), integers as
 * numbers, booleans as true/false.
 */
final class SettingCatalog
{
    public const STRING = 'string';

    public const DECIMAL = 'decimal';

    public const INTEGER = 'integer';

    public const BOOLEAN = 'boolean';

    public const ENUM = 'enum';

    /**
     * @return list<array{key: string, label: string, items: list<array{key: string, field: string, label: string, help: ?string,
     *     type: string, min: int|null, max: int|null, options: array<string, string>, default: mixed, globalOnly: bool}>}>
     */
    public static function groups(): array
    {
        return [
            ['key' => 'receipt', 'label' => 'Struk', 'items' => [
                self::item('receipt.store_name', 'Nama toko', self::STRING, max: 100, help: 'Baris 1 kepala struk. Kosong = tidak dicetak.'),
                self::item('receipt.address', 'Alamat', self::STRING, max: 200),
                self::item('receipt.city', 'Kota', self::STRING, max: 100),
                self::item('receipt.phone', 'Telepon', self::STRING, max: 50, help: 'Dicetak sebagai "Telp. ...".'),
                self::item('receipt.footer_1', 'Footer 1', self::STRING, max: 100, default: ReceiptFormatter::DEFAULT_FOOTER),
                self::item('receipt.footer_2', 'Footer 2', self::STRING, max: 100),
            ]],
            ['key' => 'sales', 'label' => 'Penjualan', 'items' => [
                self::item('rounding.cash_step', 'Pembulatan tunai (kelipatan)', self::DECIMAL, min: 0, max: 100000, default: '0',
                    help: 'Total tunai dibulatkan ke kelipatan ini, misalnya 100 atau 500. 0 = tidak dibulatkan.'),
                self::item('rounding.cash_mode', 'Arah pembulatan', self::ENUM, default: 'nearest',
                    options: ['nearest' => 'Terdekat', 'up' => 'Ke atas', 'down' => 'Ke bawah']),
                self::item('currency.decimals', 'Desimal mata uang', self::INTEGER, min: 0, max: 4, default: 0,
                    help: 'Jumlah angka di belakang koma untuk diskon dan total baris.'),
                self::item('sales.cashier_history_days', 'Batas hari riwayat kasir', self::INTEGER, min: 0, max: 365, default: 15,
                    help: 'Kasir tanpa izin sale.history hanya melihat penjualan sejak hari ini dikurangi angka ini.'),
                self::item('returns.window_days', 'Batas hari retur', self::INTEGER, min: 0, max: 365, default: 7,
                    help: 'Retur setelah batas ini perlu persetujuan supervisor.'),
                self::item('pricing.uniform', 'Harga seragam semua cabang', self::BOOLEAN, default: false, globalOnly: true,
                    help: 'Ya = harga per cabang disembunyikan di pusat dan diabaikan oleh toko.'),
            ]],
            ['key' => 'stock', 'label' => 'Stok', 'items' => [
                self::item('stock.expiry_warning_days', 'Batas hari peringatan expired', self::INTEGER, min: 0, max: 365, default: 30,
                    help: 'Stok yang expired dalam sekian hari ditandai HAMPIR EXPIRED.'),
            ]],
            ['key' => 'shift', 'label' => 'Shift', 'items' => [
                self::item('shift.blind_count', 'Blind count saat tutup shift', self::BOOLEAN, default: true,
                    help: 'Kasir mengisi kas fisik dulu sebelum angka sistem ditampilkan.'),
            ]],
        ];
    }

    /** @return array<string, array{key: string, field: string, label: string, help: ?string, type: string, min: int|null, max: int|null, options: array<string, string>, default: mixed, globalOnly: bool}> */
    public static function items(): array
    {
        $items = [];
        foreach (self::groups() as $group) {
            foreach ($group['items'] as $item) {
                $items[$item['key']] = $item;
            }
        }

        return $items;
    }

    /**
     * Validation rules for one setting's submitted value (form field = key with "_" for ".").
     *
     * @param  array{type: string, min: int|null, max: int|null, options: array<string, string>}  $item
     * @return list<mixed>
     */
    public static function rules(array $item): array
    {
        return match ($item['type']) {
            self::STRING => ['nullable', 'string', 'max:'.$item['max']],
            self::DECIMAL => ['required', 'regex:/^\d+(\.\d{1,4})?$/', 'numeric', 'min:'.$item['min'], 'max:'.$item['max']],
            self::INTEGER => ['required', 'integer', 'min:'.$item['min'], 'max:'.$item['max']],
            self::BOOLEAN => ['required', 'boolean'],
            default => ['required', Rule::in(array_keys($item['options']))],
        };
    }

    /**
     * The validated form value as stored JSON.
     *
     * @param  array{type: string}  $item
     */
    public static function encode(array $item, mixed $value): string
    {
        $typed = match ($item['type']) {
            self::STRING => trim(is_scalar($value) ? (string) $value : ''),
            self::DECIMAL => (string) Decimal::of(is_int($value) ? $value : (string) $value)->strippedOfTrailingZeros(),   // "100.00" → "100"
            self::INTEGER => (int) $value,
            self::BOOLEAN => filter_var($value, FILTER_VALIDATE_BOOLEAN),
            default => (string) $value,
        };

        return json_encode($typed, JSON_THROW_ON_ERROR | JSON_UNESCAPED_UNICODE);
    }

    /**
     * A stored JSON value as the form shows it (tolerant like the node: numbers as strings, "true"/1 for booleans);
     * null when the JSON does not fit the type.
     *
     * @param  array{type: string, options: array<string, string>}  $item
     */
    public static function decode(array $item, ?string $json): mixed
    {
        if ($json === null) {
            return null;
        }
        $value = json_decode($json, true);

        return match ($item['type']) {
            self::STRING => is_scalar($value) ? (string) $value : null,
            self::DECIMAL => is_int($value) || (is_string($value) && preg_match('/^-?\d+(\.\d+)?$/', $value)) ? (string) $value : null,
            self::INTEGER => is_int($value) || (is_string($value) && preg_match('/^-?\d+$/', $value)) ? (int) $value : null,
            self::BOOLEAN => $value === true || $value === 1 || $value === '1' || $value === 'true' ? true
                : ($value === false || $value === 0 || $value === '0' || $value === 'false' ? false : null),
            default => is_string($value) && array_key_exists($value, $item['options']) ? $value : null,
        };
    }

    /**
     * @param  array<string, string>  $options
     * @return array{key: string, field: string, label: string, help: ?string, type: string, min: int|null, max: int|null, options: array<string, string>, default: mixed, globalOnly: bool}
     */
    private static function item(string $key, string $label, string $type, ?int $min = null, ?int $max = null, mixed $default = null,
        array $options = [], bool $globalOnly = false, ?string $help = null): array
    {
        return [
            'key' => $key,
            'field' => str_replace('.', '_', $key),
            'label' => $label,
            'help' => $help,
            'type' => $type,
            'min' => $min,
            'max' => $max,
            'options' => $options,
            'default' => $default ?? ($type === self::STRING ? '' : null),
            'globalOnly' => $globalOnly,
        ];
    }
}
