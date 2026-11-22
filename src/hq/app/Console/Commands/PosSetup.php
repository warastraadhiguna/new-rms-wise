<?php

namespace App\Console\Commands;

use App\Http\Controllers\Settings\UserController;
use App\Support\Setup\SystemSetup;
use Illuminate\Console\Command;
use Illuminate\Support\Facades\DB;

/**
 * Production setup, safe to repeat: units, payment methods (TUNAI, DEBIT, QRIS, TRANSFER, KREDIT, RETUR), standard
 * reasons, standard roles with default permissions and default settings — only what is missing. When no active OWNER
 * exists it creates one from --owner-username / --owner-password (or asks).
 */
class PosSetup extends Command
{
    protected $signature = 'pos:setup
        {--owner-username= : Username of the first OWNER (only when no active OWNER exists)}
        {--owner-password= : Password of the first OWNER (at least 8 characters)}
        {--owner-name=Pemilik : Full name of the first OWNER}';

    protected $description = 'Create the system data every installation needs (idempotent)';

    public function handle(SystemSetup $setup): int
    {
        $created = $setup->run();
        $summary = implode(', ', array_map(fn (string $kind, int $n) => "{$kind} {$n}", array_keys($created), $created));
        $this->components->info(array_sum($created) === 0 ? 'System data complete; nothing to add.' : "Created: {$summary}.");

        if ($setup->hasActiveOwner()) {
            return self::SUCCESS;
        }

        $username = (string) ($this->option('owner-username') ?? '');
        $password = (string) ($this->option('owner-password') ?? '');
        if ($username === '' && $this->input->isInteractive()) {
            $username = (string) $this->ask('No active OWNER yet. Username for the first OWNER');
            $password = (string) $this->secret('Password (at least '.UserController::MIN_PASSWORD.' characters)');
        }
        if ($username === '') {
            $this->components->warn('No active OWNER: run again with --owner-username and --owner-password.');

            return self::SUCCESS;
        }
        if (mb_strlen($password) < UserController::MIN_PASSWORD || ! preg_match('/^[a-z0-9._-]{3,50}$/', mb_strtolower(trim($username)))) {
            $this->components->error('Username: 3-50 of a-z 0-9 . _ -; password: at least '.UserController::MIN_PASSWORD.' characters.');

            return self::FAILURE;
        }
        if (DB::table('users')->where('username', mb_strtolower(trim($username)))->exists()) {
            $this->components->error("Username {$username} already exists.");

            return self::FAILURE;
        }
        $setup->createOwner($username, $password, (string) $this->option('owner-name'));
        $this->components->info("OWNER {$username} created.");

        return self::SUCCESS;
    }
}
