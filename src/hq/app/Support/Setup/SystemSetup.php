<?php

namespace App\Support\Setup;

use App\Auth\OwnerGuard;
use App\Http\Controllers\Settings\PaymentMethodController;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use App\Support\Settings\SettingCatalog;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Hash;

/**
 * The system data every installation needs (php artisan pos:setup), written through the master data writer and safe to
 * repeat: only what is missing is created, nothing an admin changed is overwritten (a role keeps its edited
 * permissions, a setting its value, a payment method its name).
 */
final class SystemSetup
{
    /** code => [name, allow_decimal] */
    public const UNITS = ['PCS' => ['Pieces', false], 'BOX' => ['Box', false], 'PAK' => ['Pak', false], 'KG' => ['Kilogram', true], 'LTR' => ['Liter', true]];

    /** code => [name, is_cash, is_credit] — the node's codes (DevSeeder, SaleService). */
    public const PAYMENT_METHODS = [
        'CASH' => ['TUNAI', true, false],
        'DEBIT' => ['DEBIT', false, false],
        'QRIS' => ['QRIS', false, false],
        'TRANSFER' => ['TRANSFER', false, false],
        'CREDIT' => ['KREDIT', false, true],
        PaymentMethodController::RETURN => ['RETUR', false, false],
    ];

    public const REASONS = [
        'SALE_VOID' => ['Salah input', 'Pembeli batal', 'Salah harga'],
        'RETURN' => ['Barang rusak', 'Salah barang', 'Kedaluwarsa', 'Tidak cocok'],
        'ADJUSTMENT' => ['Saldo awal (OPENING)', 'Barang rusak', 'Hilang', 'Kedaluwarsa', 'Selisih stok opname'],
    ];

    /** code => [name, permissions] — the node's defaults (DevSeeder) plus HQ_ADMIN for the web. */
    public const ROLES = [
        OwnerGuard::ROLE_CODE => ['Pemilik', ['*']],
        'HQ_ADMIN' => ['Admin pusat', ['hq.access', 'master.view', 'master.edit', 'users.manage', 'settings.manage', 'stock.view']],
        'STORE_ADMIN' => ['Admin toko / supervisor', ['shift.open', 'shift.close', 'shift.share', 'sale.create', 'sale.discount.override',
            'sale.line.delete', 'discount.max:100', 'stock.view', 'stock.transfer', 'sale.history', 'sale.void', 'sale.return.late']],
        'CASHIER' => ['Kasir', ['shift.open', 'sale.create']],
        'WAREHOUSE_STAFF' => ['Staf gudang', ['stock.view', 'stock.transfer']],
    ];

    public function __construct(private readonly MasterDataWriter $writer) {}

    /** @return array<string, int> what was created, per kind */
    public function run(): array
    {
        $context = WriteContext::system();
        $created = ['units' => 0, 'payment_methods' => 0, 'reasons' => 0, 'roles' => 0, 'settings' => 0];

        DB::transaction(function () use ($context, &$created) {
            foreach (self::UNITS as $code => [$name, $decimal]) {
                if (! DB::table('units')->where('code', $code)->exists()) {
                    $this->writer->create('units', ['code' => $code, 'name' => $name, 'allow_decimal' => $decimal], $context);
                    $created['units']++;
                }
            }
            foreach (self::PAYMENT_METHODS as $code => [$name, $cash, $credit]) {
                if (! DB::table('payment_methods')->where('code', $code)->exists()) {
                    $this->writer->create('payment_methods', ['code' => $code, 'name' => $name, 'is_cash' => $cash, 'is_credit' => $credit], $context);
                    $created['payment_methods']++;
                }
            }
            foreach (self::REASONS as $type => $names) {
                foreach ($names as $name) {
                    if (! DB::table('reasons')->where('type', $type)->where('name', $name)->whereNull('deleted_at')->exists()) {
                        $this->writer->create('reasons', ['type' => $type, 'name' => $name], $context);
                        $created['reasons']++;
                    }
                }
            }
            foreach (self::ROLES as $code => [$name, $permissions]) {
                if (! DB::table('roles')->where('code', $code)->exists()) {
                    $this->writer->create('roles', ['code' => $code, 'name' => $name, 'permissions' => json_encode($permissions, JSON_THROW_ON_ERROR)], $context);
                    $created['roles']++;
                }
            }
            foreach (SettingCatalog::items() as $key => $item) {
                if ($item['default'] === '' || DB::table('settings')->whereNull('branch_id')->where('setting_key', $key)->exists()) {
                    continue;                                   // empty texts stay missing; existing rows are kept
                }
                $this->writer->create('settings', ['branch_id' => null, 'setting_key' => $key, 'setting_value' => SettingCatalog::encode($item, $item['default'])], $context);
                $created['settings']++;
            }
        });

        return $created;
    }

    public function hasActiveOwner(): bool
    {
        return app(OwnerGuard::class)->activeOwners() > 0;
    }

    /** Creates the first OWNER (for all nodes); the username must be new. Returns the user id. */
    public function createOwner(string $username, string $password, string $fullName): string
    {
        $context = WriteContext::system();
        $role = (string) DB::table('roles')->where('code', OwnerGuard::ROLE_CODE)->whereNull('deleted_at')->value('id');

        return DB::transaction(function () use ($username, $password, $fullName, $role, $context) {
            $user = $this->writer->create('users', [
                'username' => mb_strtolower(trim($username)),
                'password_hash' => Hash::make($password),
                'full_name' => $fullName,
            ], $context);
            $this->writer->create('user_roles', ['user_id' => $user, 'role_id' => $role, 'node_id' => null], $context);

            return $user;
        });
    }
}
