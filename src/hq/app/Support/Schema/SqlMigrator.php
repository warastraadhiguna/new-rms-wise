<?php

namespace App\Support\Schema;

use Closure;
use Illuminate\Database\Connection;
use RuntimeException;

/**
 * Applies database/migrations/NNNN_*.sql — the ONLY source of the domain schema, shared with the Node Service —
 * in name order. The journal is the one DbUp writes at the nodes: table schema_versions
 * (schemaversionid, scriptname, applied), scriptname = the file name, applied = UTC. So one database can be
 * migrated from either side without running a script twice. Each script runs in a UTC session.
 */
final class SqlMigrator
{
    public const JOURNAL_TABLE = 'schema_versions';

    /** Same DDL as DbUp's MySqlTableJournal. */
    private const JOURNAL_DDL = <<<'SQL'
        CREATE TABLE schema_versions
        (
            `schemaversionid` INT NOT NULL AUTO_INCREMENT,
            `scriptname` VARCHAR(255) NOT NULL,
            `applied` TIMESTAMP NOT NULL,
            PRIMARY KEY (`schemaversionid`)
        )
        SQL;

    private const LOCK_NAME = 'pos_schema_migration';

    public function __construct(private readonly Connection $db, private readonly string $path) {}

    /**
     * @return list<string> scripts applied by this run (empty when the database is up to date)
     *
     * @throws RuntimeException a script failed; the scripts before it stay applied, nothing after it runs
     */
    public function migrate(?Closure $onScript = null): array
    {
        if (! is_dir($this->path)) {
            throw new RuntimeException("Migrations folder not found: {$this->path}");
        }

        // Two HQ processes never migrate at the same time (DbUp at a node has its own database).
        if ((int) $this->db->selectOne('SELECT GET_LOCK(?, 60) AS l', [self::LOCK_NAME])->l !== 1) {
            throw new RuntimeException('Another schema migration is running.');
        }

        try {
            $this->ensureJournal();
            $done = array_flip($this->appliedScripts());
            $applied = [];

            foreach ($this->scripts() as $name => $file) {
                if (isset($done[$name])) {
                    continue;
                }
                if ($onScript !== null) {
                    $onScript($name);
                }
                $this->run($name, (string) file_get_contents($file));
                $applied[] = $name;
            }

            return $applied;
        } finally {
            $this->db->select('SELECT RELEASE_LOCK(?)', [self::LOCK_NAME]);
        }
    }

    /** @return list<string> scripts recorded in the journal, in the order they ran */
    public function appliedScripts(): array
    {
        if (! $this->journalExists()) {
            return [];
        }

        $names = $this->db->table(self::JOURNAL_TABLE)
            ->orderBy('schemaversionid')
            ->pluck('scriptname')
            ->map(fn (mixed $name) => is_scalar($name) ? (string) $name : '')
            ->all();

        return array_values($names);
    }

    /** @return list<string> scripts in the folder that the journal does not have yet */
    public function pendingScripts(): array
    {
        $done = array_flip($this->appliedScripts());

        return array_values(array_filter(array_keys($this->scripts()), fn (string $name) => ! isset($done[$name])));
    }

    /** "0001_initial_schema.sql" → "0001". */
    public static function versionOf(string $scriptName): string
    {
        return preg_match('/^(\d+)_/', $scriptName, $m) ? $m[1] : $scriptName;
    }

    /** @return array<string, string> file name => path, ordinal name order like DbUp */
    private function scripts(): array
    {
        $files = glob(rtrim($this->path, '/\\').DIRECTORY_SEPARATOR.'*.sql') ?: [];
        $scripts = [];
        foreach ($files as $file) {
            $scripts[basename($file)] = $file;
        }
        ksort($scripts, SORT_STRING);

        return $scripts;
    }

    private function run(string $name, string $sql): void
    {
        // Script text from the shared migrations folder, run statement by statement as DbUp does.
        $pdo = $this->db->getPdo();
        $pdo->exec("SET time_zone = '+00:00'");
        foreach (SqlSplitter::split($sql) as $index => $statement) {
            try {
                $pdo->exec($statement);
            } catch (\Throwable $e) {
                throw new RuntimeException("Migration {$name} failed at statement ".($index + 1).': '.$e->getMessage(), 0, $e);
            }
        }
        $this->db->insert('INSERT INTO '.self::JOURNAL_TABLE.' (scriptname, applied) VALUES (?, UTC_TIMESTAMP())', [$name]);
    }

    private function ensureJournal(): void
    {
        if (! $this->journalExists()) {
            $this->db->unprepared(self::JOURNAL_DDL);
        }
    }

    private function journalExists(): bool
    {
        return $this->db->selectOne(
            'SELECT COUNT(*) AS c FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = ?',
            [self::JOURNAL_TABLE],
        )->c > 0;
    }
}
