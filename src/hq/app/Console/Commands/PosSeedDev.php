<?php

namespace App\Console\Commands;

use App\Http\Controllers\MasterData\NodeController;
use App\Support\MasterData\CodeGenerator;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use App\Support\Setup\SystemSetup;
use Carbon\CarbonImmutable;
use Illuminate\Console\Command;
use Illuminate\Support\Facades\DB;

/**
 * Development data for the HQ web: first pos:setup (system data), then the user admin / admin123 (OWNER for all
 * nodes) and sample data — entity E01, branch B01 (Asia/Jakarta), nodes HQ and TK01 (with DISPLAY and DAMAGED),
 * a few products with barcodes and prices, a customer with credit and a supplier. Everything through the master data
 * writer. Refuses databases not named *_dev / *_test; safe to repeat.
 */
class PosSeedDev extends Command
{
    public const ADMIN_USERNAME = 'admin';

    public const ADMIN_PASSWORD = 'admin123';

    /** sku => [name, base unit, barcode, price] */
    private const PRODUCTS = [
        'DEV-0001' => ['Teh Botol Sosro 450 ml', 'PCS', '8991001000011', '4000'],
        'DEV-0002' => ['Indomie Goreng', 'PCS', '8991001000028', '3500'],
        'DEV-0003' => ['Gula Pasir', 'KG', '8991001000035', '16000'],
        'DEV-0004' => ['Aqua 600 ml', 'PCS', '8991001000042', '3000'],
        'DEV-0005' => ['Beras Premium', 'KG', '8991001000059', '14000'],
    ];

    protected $signature = 'pos:seed-dev';

    protected $description = 'pos:setup plus HQ development data (admin/admin123, entity, branch, nodes HQ and TK01, sample products)';

    public function handle(MasterDataWriter $writer, SystemSetup $setup): int
    {
        $database = (string) DB::connection()->getDatabaseName();
        if (! preg_match('/_(dev|test)$/', $database)) {
            $this->components->error("Refusing to seed '{$database}': only *_dev or *_test databases.");

            return self::FAILURE;
        }

        $this->call('pos:setup', ['--no-interaction' => true]);
        $context = WriteContext::system();

        if (! DB::table('users')->where('username', self::ADMIN_USERNAME)->exists()) {
            $setup->createOwner(self::ADMIN_USERNAME, self::ADMIN_PASSWORD, 'Administrator');
        }

        if (DB::table('nodes')->where('code', 'HQ')->exists()) {
            $this->components->info('Dev data already present (node HQ exists); nothing to do.');

            return self::SUCCESS;
        }

        DB::transaction(function () use ($writer, $context) {
            $entity = $writer->create('entities', ['code' => 'E01', 'name' => 'PT Dev Retail'], $context);
            $branch = $writer->create('branches', [
                'entity_id' => $entity, 'code' => 'B01', 'name' => 'Cabang Dev', 'timezone' => 'Asia/Jakarta',
            ], $context);
            $writer->create('nodes', ['branch_id' => $branch, 'code' => 'HQ', 'name' => 'Kantor Pusat', 'type' => 'HQ'], $context);
            $store = $writer->create('nodes', ['branch_id' => $branch, 'code' => 'TK01', 'name' => 'Toko Dev 1', 'type' => 'STORE'], $context);
            foreach (NodeController::DEFAULT_LOCATIONS['STORE'] as [$code, $name, $type, $sellable]) {
                $writer->create('locations', ['node_id' => $store, 'code' => $code, 'name' => $name, 'type' => $type, 'is_sellable' => $sellable], $context);
            }

            $admin = (string) DB::table('users')->where('username', self::ADMIN_USERNAME)->value('id');
            $from = CarbonImmutable::now('UTC')->subDay()->format('Y-m-d H:i:s.v');
            foreach (self::PRODUCTS as $sku => [$name, $unitCode, $barcode, $price]) {
                if (DB::table('products')->where('sku', $sku)->exists()) {
                    continue;
                }
                $unit = (string) DB::table('units')->where('code', $unitCode)->value('id');
                $product = $writer->create('products', ['sku' => $sku, 'name' => $name, 'base_unit_id' => $unit, 'status' => 'ACTIVE'], $context);
                $pu = $writer->create('product_units', [
                    'product_id' => $product, 'unit_id' => $unit, 'conversion' => '1.0000', 'is_default_sale' => true, 'is_default_purchase' => true,
                ], $context);
                $writer->create('product_barcodes', ['product_unit_id' => $pu, 'barcode' => $barcode], $context);
                $writer->create('prices', [
                    'product_unit_id' => $pu, 'branch_id' => null, 'price' => $price.'.0000', 'min_qty' => '1.0000',
                    'effective_from' => $from, 'created_by' => $admin,
                ], $context);
            }

            $writer->create('contacts', [
                'code' => CodeGenerator::next('contacts', 'code', 'C'), 'is_customer' => true, 'name' => 'Budi Pelanggan', 'phone' => '081200000001',
                'credit_limit' => '500000.0000', 'payment_term_days' => 14,
            ], $context);
            $writer->create('contacts', ['code' => CodeGenerator::next('contacts', 'code', 'S'), 'is_supplier' => true, 'name' => 'PT Sumber Dev', 'bank_account' => 'BCA 1234567890'], $context);
        });

        $this->components->info('Dev data created: nodes HQ and TK01, 5 products, user '.self::ADMIN_USERNAME.' / '.self::ADMIN_PASSWORD.' (OWNER).');

        return self::SUCCESS;
    }
}
