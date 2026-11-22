<?php

namespace App\Sync;

/**
 * Node documents HQ accepts (docs/sync-contract.md) and how each table's rows belong to the document:
 *   root            the document row itself: id = doc_id, node_id = the token's node
 *   parent:<column> a line: <column> = doc_id
 *   doc             a ledger row: doc_id = doc_id (and node_id = the node)
 *   device          the device row the root points to (root.device_id), node_id = the node
 * Tables are applied in the listed order (foreign keys). Must match Pos.NodeService DocumentSnapshot.
 */
final class DocumentTypes
{
    public const ROOT = 'root';

    public const DOC = 'doc';

    public const DEVICE = 'device';

    /** @var array<string, array{mutable: bool, tables: array<string, string>}> */
    public const TYPES = [
        'CASHIER_SHIFT' => ['mutable' => true, 'tables' => ['devices' => self::DEVICE, 'cashier_shifts' => self::ROOT]],
        'SALE' => ['mutable' => false, 'tables' => [
            'sales' => self::ROOT, 'sale_lines' => 'parent:sale_id', 'sale_payments' => 'parent:sale_id',
            'stock_ledger' => self::DOC, 'loyalty_point_ledger' => self::DOC,
        ]],
        'SALE_VOID' => ['mutable' => false, 'tables' => [
            'sale_voids' => self::ROOT, 'stock_ledger' => self::DOC, 'loyalty_point_ledger' => self::DOC,
        ]],
        'SALE_RETURN' => ['mutable' => false, 'tables' => [
            'devices' => self::DEVICE, 'sale_returns' => self::ROOT, 'sale_return_lines' => 'parent:sale_return_id',
            'stock_ledger' => self::DOC, 'loyalty_point_ledger' => self::DOC,
        ]],
        'CART_CANCELLATION' => ['mutable' => false, 'tables' => [
            'devices' => self::DEVICE, 'cart_cancellations' => self::ROOT, 'cart_cancellation_lines' => 'parent:cart_cancellation_id',
        ]],
        'LOCATION_TRANSFER' => ['mutable' => false, 'tables' => [
            'location_transfers' => self::ROOT, 'location_transfer_lines' => 'parent:location_transfer_id', 'stock_ledger' => self::DOC,
        ]],
        'STOCK_ADJUSTMENT' => ['mutable' => false, 'tables' => [
            'stock_adjustments' => self::ROOT, 'stock_adjustment_lines' => 'parent:stock_adjustment_id', 'stock_ledger' => self::DOC,
        ]],
    ];

    /** @return array{mutable: bool, tables: array<string, string>}|null */
    public static function spec(string $docType): ?array
    {
        return self::TYPES[$docType] ?? null;
    }

    public static function root(string $docType): ?string
    {
        foreach (self::TYPES[$docType]['tables'] ?? [] as $table => $link) {
            if ($link === self::ROOT) {
                return $table;
            }
        }

        return null;
    }
}
