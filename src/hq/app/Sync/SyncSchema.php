<?php

namespace App\Sync;

use Illuminate\Support\Facades\DB;

/** Columns of the synced tables as MySQL knows them, without generated columns (never sent, never written). */
final class SyncSchema
{
    /** HQ-owned master data, in an order that is safe for foreign keys (docs/sync-contract.md). */
    public const MASTER_TABLES = [
        'entities', 'branches', 'nodes', 'locations', 'roles', 'users', 'user_roles', 'units', 'categories', 'brands',
        'products', 'product_units', 'product_barcodes', 'prices', 'promotions', 'contacts', 'sales_reps',
        'supplier_products', 'payment_methods', 'reasons', 'expense_types', 'settings',
    ];

    /** Master tables with a branch_id: a node only gets the global rows and those of its own branch. */
    public const BRANCH_FILTERED = ['prices', 'settings', 'promotions'];

    /** @var array<string, list<string>> */
    private array $columns = [];

    /** @return list<string> */
    public function columns(string $table): array
    {
        if (! isset($this->columns[$table])) {
            $this->columns[$table] = array_values(array_map(
                fn (object $c) => (string) ((array) $c)['name'],
                DB::select(
                    "SELECT COLUMN_NAME AS name FROM information_schema.COLUMNS
                     WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = ? AND EXTRA NOT LIKE '%GENERATED%'
                     ORDER BY ORDINAL_POSITION",
                    [$table],
                ),
            ));
        }

        return $this->columns[$table];
    }
}
