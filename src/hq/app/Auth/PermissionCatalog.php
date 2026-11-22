<?php

namespace App\Auth;

use App\Support\Decimal;

/**
 * The official list of permissions the system knows (HQ web and Node Service), with Indonesian descriptions for the
 * Role page. A role stores a JSON array of these names; "*" grants everything. Parameterised permissions such as
 * discount.max:N carry a number. A test keeps this list in step with Pos.Shared.Auth.Permissions in C#.
 */
final class PermissionCatalog
{
    public const ALL = '*';

    public const DISCOUNT_MAX = 'discount.max';

    /** @return list<array{group: string, items: list<array{name: string, label: string}>}> */
    public static function groups(): array
    {
        return [
            ['group' => 'Web pusat', 'items' => [
                ['name' => 'hq.access', 'label' => 'Masuk ke web pusat'],
                ['name' => 'master.view', 'label' => 'Melihat master data (barang, kontak, toko)'],
                ['name' => 'master.edit', 'label' => 'Mengubah master data'],
                ['name' => 'users.manage', 'label' => 'Mengelola pengguna dan role'],
                ['name' => 'settings.manage', 'label' => 'Mengubah pengaturan dan data pendukung'],
            ]],
            ['group' => 'Kasir dan shift', 'items' => [
                ['name' => 'shift.open', 'label' => 'Membuka shift kasir'],
                ['name' => 'shift.close', 'label' => 'Menutup shift milik kasir lain'],
                ['name' => 'shift.share', 'label' => 'Berjualan di shift milik kasir lain'],
                ['name' => 'sale.create', 'label' => 'Berjualan (Form Penjualan)'],
                ['name' => 'sale.history', 'label' => 'Melihat Data Penjualan tanpa batas hari'],
            ]],
            ['group' => 'Persetujuan supervisor', 'items' => [
                ['name' => 'sale.discount.override', 'label' => 'Menyetujui diskon di atas batas kasir'],
                ['name' => 'sale.line.delete', 'label' => 'Menyetujui hapus barang dari transaksi'],
                ['name' => 'sale.void', 'label' => 'Void penjualan (tanpa persetujuan)'],
                ['name' => 'sale.return.late', 'label' => 'Menyetujui retur lewat batas hari'],
            ]],
            ['group' => 'Stok', 'items' => [
                ['name' => 'stock.view', 'label' => 'Melihat stok, kartu stok, dan laporan stok'],
                ['name' => 'stock.transfer', 'label' => 'Mutasi stok antar lokasi'],
                ['name' => 'stock.rebuild', 'label' => 'Memperbaiki saldo stok dari kartu stok'],
            ]],
        ];
    }

    /** @return list<string> every plain permission name (without "*" and parameterised ones) */
    public static function names(): array
    {
        $names = [];
        foreach (self::groups() as $group) {
            foreach ($group['items'] as $item) {
                $names[] = $item['name'];
            }
        }

        return $names;
    }

    /** Valid name: "*", a plain permission from the list, or discount.max:N with 0 <= N <= 100. */
    public static function isValid(string $permission): bool
    {
        if ($permission === self::ALL || in_array($permission, self::names(), true)) {
            return true;
        }

        return self::discountMax($permission) !== null;
    }

    /** N of "discount.max:N" (0–100, up to 2 decimals), or null when it is not that permission. */
    public static function discountMax(string $permission): ?string
    {
        if (! preg_match('/^discount\.max:(\d{1,3}(?:\.\d{1,2})?)$/', $permission, $m)) {
            return null;
        }

        return Decimal::compare($m[1], '100') <= 0 ? $m[1] : null;
    }
}
