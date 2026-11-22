<?php

namespace App\Support\Catalog;

use Illuminate\Support\Facades\DB;

/**
 * Has a product ever been used? Every table with a product_id or product_unit_id column counts (sales, returns,
 * cancellations, transfers, purchasing, stock ledger and balances, counts, costs …), except the product's own master
 * data. Found from information_schema, so a document table added by a later migration is covered automatically.
 * For now this sees the data at HQ; once the stores sync, their documents arrive here too.
 */
final class ProductUsage
{
    /** The product's own master data: not "use". */
    private const MASTER_TABLES = ['products', 'product_units', 'product_barcodes', 'prices', 'supplier_products'];

    /** Indonesian names for the message (other tables are reported by their table name). */
    private const LABELS = [
        'sale_lines' => 'penjualan',
        'sale_return_lines' => 'retur penjualan',
        'cart_cancellation_lines' => 'pembatalan transaksi',
        'stock_ledger' => 'pergerakan stok',
        'stock_balances' => 'stok',
        'location_transfer_lines' => 'mutasi lokasi',
        'purchase_order_lines' => 'pesanan pembelian',
        'goods_receipt_lines' => 'penerimaan barang',
        'purchase_invoice_lines' => 'faktur pembelian',
        'delivery_note_lines' => 'surat jalan',
        'stock_adjustment_lines' => 'penyesuaian stok',
        'stock_count_lines' => 'stok opname',
        'stock_request_lines' => 'permintaan barang',
        'customer_orders' => 'pesanan pelanggan',
        'product_costs' => 'HPP',
        'pending_cost_adjustments' => 'koreksi HPP',
    ];

    /** @var list<array{table: string, column: string}>|null */
    private static ?array $references = null;

    /** @return list<string> where the product is used (Indonesian labels); empty = never used, may be deleted */
    public function usedIn(string $productId): array
    {
        $unitIds = DB::table('product_units')->where('product_id', $productId)->pluck('id')->all();
        $found = [];
        foreach (self::references() as ['table' => $table, 'column' => $column]) {
            $ids = $column === 'product_id' ? [$productId] : $unitIds;
            if ($ids !== [] && DB::table($table)->whereIn($column, $ids)->exists()) {
                $found[$table] = self::LABELS[$table] ?? $table;
            }
        }

        return array_values(array_unique($found));
    }

    /** @return list<array{table: string, column: string}> */
    private static function references(): array
    {
        if (self::$references !== null) {
            return self::$references;
        }
        $rows = DB::table('information_schema.columns')
            ->whereRaw('table_schema = DATABASE()')
            ->whereIn('column_name', ['product_id', 'product_unit_id'])
            ->whereNotIn('table_name', self::MASTER_TABLES)
            ->orderBy('table_name')
            ->selectRaw('table_name AS t, column_name AS c')
            ->get();
        $references = [];
        foreach ($rows as $row) {
            $references[] = ['table' => is_scalar($row->t ?? null) ? (string) $row->t : '', 'column' => is_scalar($row->c ?? null) ? (string) $row->c : ''];
        }

        return self::$references = $references;
    }
}
