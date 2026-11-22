<?php

namespace App\Console\Commands;

use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Console\Command;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Hash;

/**
 * Development data for the HQ web: entity E01, branch B01 (Asia/Jakarta), nodes HQ and TK01, role OWNER with "*",
 * user admin / admin123 with OWNER at all nodes. Written through the master data writer (versions + audit), like any
 * other master data. Refuses databases not named *_dev / *_test; safe to repeat.
 */
class PosSeedDev extends Command
{
    public const ADMIN_USERNAME = 'admin';

    public const ADMIN_PASSWORD = 'admin123';

    protected $signature = 'pos:seed-dev';

    protected $description = 'Seed HQ development data (entity, branch, nodes HQ and TK01, role OWNER, user admin/admin123, units PCS/BOX/KG)';

    public function handle(MasterDataWriter $writer): int
    {
        $database = (string) DB::connection()->getDatabaseName();
        if (! preg_match('/_(dev|test)$/', $database)) {
            $this->components->error("Refusing to seed '{$database}': only *_dev or *_test databases.");

            return self::FAILURE;
        }

        $context = WriteContext::system();
        $units = 0;
        foreach ([['PCS', 'Pieces', false], ['BOX', 'Box', false], ['KG', 'Kilogram', true]] as [$code, $name, $decimal]) {
            if (! DB::table('units')->where('code', $code)->exists()) {
                $writer->create('units', ['code' => $code, 'name' => $name, 'allow_decimal' => $decimal], $context);
                $units++;
            }
        }

        if (DB::table('nodes')->where('code', 'HQ')->exists()) {
            $this->components->info('Dev data already present (node HQ exists); '.($units === 0 ? 'nothing to do.' : "{$units} unit(s) added."));

            return self::SUCCESS;
        }

        $entity = $writer->create('entities', ['code' => 'E01', 'name' => 'PT Dev Retail'], $context);
        $branch = $writer->create('branches', [
            'entity_id' => $entity, 'code' => 'B01', 'name' => 'Cabang Dev', 'timezone' => 'Asia/Jakarta',
        ], $context);
        $writer->create('nodes', ['branch_id' => $branch, 'code' => 'HQ', 'name' => 'Kantor Pusat', 'type' => 'HQ'], $context);
        $writer->create('nodes', ['branch_id' => $branch, 'code' => 'TK01', 'name' => 'Toko Dev 1', 'type' => 'STORE'], $context);

        $owner = DB::table('roles')->where('code', 'OWNER')->whereNull('deleted_at')->value('id')
            ?? $writer->create('roles', ['code' => 'OWNER', 'name' => 'Pemilik', 'permissions' => json_encode(['*'])], $context);
        $admin = DB::table('users')->where('username', self::ADMIN_USERNAME)->value('id')
            ?? $writer->create('users', [
                'username' => self::ADMIN_USERNAME,
                'password_hash' => Hash::make(self::ADMIN_PASSWORD),
                'full_name' => 'Administrator',
            ], $context);
        $writer->create('user_roles', ['user_id' => $admin, 'role_id' => $owner, 'node_id' => null], $context);

        $this->components->info('Dev data created: nodes HQ and TK01, user '.self::ADMIN_USERNAME.' / '.self::ADMIN_PASSWORD.' (OWNER).');

        return self::SUCCESS;
    }
}
