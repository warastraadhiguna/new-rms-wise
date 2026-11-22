<?php

namespace App\Console\Commands;

use App\Support\Schema\SqlMigrator;
use Illuminate\Console\Command;
use Illuminate\Support\Facades\DB;
use RuntimeException;

/**
 * Applies the shared SQL migrations (database/migrations/*.sql) to the HQ database, journaled in schema_versions
 * exactly like DbUp at the nodes. Idempotent: scripts already in the journal (from either side) are skipped.
 */
class PosMigrate extends Command
{
    protected $signature = 'pos:migrate {--status : Only list applied and pending scripts}';

    protected $description = 'Apply database/migrations/*.sql (domain schema) with the DbUp-compatible journal schema_versions';

    public function handle(): int
    {
        $migrator = new SqlMigrator(DB::connection(), (string) config('pos.migrations_path'));

        if ($this->option('status')) {
            foreach ($migrator->appliedScripts() as $name) {
                $this->line("  applied  {$name}");
            }
            foreach ($migrator->pendingScripts() as $name) {
                $this->line("  pending  {$name}");
            }

            return self::SUCCESS;
        }

        try {
            $applied = $migrator->migrate(fn (string $name) => $this->line("  applying {$name}"));
        } catch (RuntimeException $e) {
            $this->components->error($e->getMessage());

            return self::FAILURE;
        }

        $applied === []
            ? $this->components->info('Domain schema is up to date.')
            : $this->components->info(count($applied).' script(s) applied.');

        return self::SUCCESS;
    }
}
