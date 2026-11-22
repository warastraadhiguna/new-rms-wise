<?php

use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use App\Sync\NodeTokens;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Str;
use Illuminate\Testing\TestResponse;

/* Helpers for the sync API and the dashboard: a node with a token, HQ products, node documents in the push format. */

function id7(): string
{
    return (string) Str::uuid7();
}

/** A store node with its own branch, a DISPLAY location and a sync token. */
function syncNode(bool $active = true): object
{
    $writer = app(MasterDataWriter::class);
    $ctx = WriteContext::system();
    $branch = newBranch();
    $node = $writer->create('nodes', ['branch_id' => $branch, 'code' => 'T'.substr(uid(), -8), 'name' => 'Toko Sinkron', 'type' => 'STORE', 'is_active' => $active], $ctx);
    $location = $writer->create('locations', ['node_id' => $node, 'code' => 'DISPLAY', 'name' => 'Display', 'type' => 'DISPLAY', 'is_sellable' => true], $ctx);

    return (object) ['id' => $node, 'branch' => $branch, 'location' => $location, 'token' => app(NodeTokens::class)->create($node, null)];
}

/** A product with its base unit at HQ: [product id, product unit id]. */
function syncProduct(): array
{
    $writer = app(MasterDataWriter::class);
    $ctx = WriteContext::system();
    $unit = $writer->create('units', ['code' => uid('U'), 'name' => 'Satuan'], $ctx);
    $product = $writer->create('products', ['sku' => uid('SKU'), 'name' => 'Barang Sinkron', 'base_unit_id' => $unit, 'status' => 'ACTIVE'], $ctx);

    return [$product, $writer->create('product_units', ['product_id' => $product, 'unit_id' => $unit, 'conversion' => '1', 'is_default_sale' => true], $ctx)];
}

function cashMethod(): string
{
    return (string) (DB::table('payment_methods')->where('code', 'CASH')->value('id')
        ?? app(MasterDataWriter::class)->create('payment_methods', ['code' => 'CASH', 'name' => 'TUNAI', 'is_cash' => true], WriteContext::system()));
}

function shiftDoc(object $node, string $shift, string $device, string $cashier, int $rowVersion = 1, string $status = 'OPEN', int $outbox = 1): array
{
    return [
        'outbox_id' => $outbox, 'doc_type' => 'CASHIER_SHIFT', 'doc_id' => $shift, 'row_version' => $rowVersion,
        'occurred_at' => '2026-11-22 01:00:00.000', 'app_version' => '1.0.0',
        'payload' => ['format' => 2, 'node_id' => $node->id, 'tables' => [
            ['table' => 'devices', 'rows' => [['id' => $device, 'node_id' => $node->id, 'name' => 'Kasir PC 1', 'type' => 'CASHIER_PC',
                'fingerprint' => 'FP-'.$device, 'public_key' => 'DEV', 'status' => 'ACTIVE']]],
            ['table' => 'cashier_shifts', 'rows' => [['id' => $shift, 'node_id' => $node->id, 'number' => 'SH-'.substr($shift, -8),
                'device_id' => $device, 'cashier_id' => $cashier, 'opened_at' => '2026-11-22 01:00:00.000',
                'closed_at' => $status === 'CLOSED' ? '2026-11-22 09:00:00.000' : null, 'opening_cash' => '100000.0000',
                'status' => $status, 'row_version' => $rowVersion]]],
        ]],
    ];
}

function saleDoc(object $node, string $sale, string $shift, string $cashier, array $product, int $outbox = 2, string $total = '8000.0000', string $soldAt = '2026-11-22 02:00:00.000'): array
{
    $line = id7();

    return [
        'outbox_id' => $outbox, 'doc_type' => 'SALE', 'doc_id' => $sale, 'row_version' => 1,
        'occurred_at' => '2026-11-22 02:00:00.000', 'app_version' => '1.0.0',
        'payload' => ['format' => 2, 'node_id' => $node->id, 'tables' => [
            ['table' => 'sales', 'rows' => [['id' => $sale, 'node_id' => $node->id, 'number' => 'SL-'.substr($sale, -10), 'shift_id' => $shift,
                'customer_id' => null, 'sold_at' => $soldAt, 'subtotal' => $total, 'discount_amount' => '0.0000',
                'tax_amount' => '0.0000', 'rounding_amount' => '0.0000', 'total' => $total, 'credit_amount' => '0.0000',
                'due_date' => null, 'created_by' => $cashier]]],
            ['table' => 'sale_lines', 'rows' => [['id' => $line, 'sale_id' => $sale, 'line_no' => 1, 'product_id' => $product[0],
                'product_unit_id' => $product[1], 'location_id' => $node->location, 'qty' => '2.0000', 'conversion' => '1.0000',
                'base_qty' => '2.0000', 'unit_price' => '4000.0000', 'discount_amount' => '0.0000', 'line_total' => $total]]],
            ['table' => 'sale_payments', 'rows' => [['id' => id7(), 'sale_id' => $sale, 'payment_method_id' => cashMethod(),
                'amount' => $total, 'tendered' => '10000.0000', 'change_given' => '2000.0000']]],
            ['table' => 'stock_ledger', 'rows' => [['id' => id7(), 'node_id' => $node->id, 'location_id' => $node->location,
                'product_id' => $product[0], 'expiry_date' => '2027-01-31', 'base_qty' => '-2.0000', 'doc_type' => 'SALE',
                'doc_id' => $sale, 'doc_line_id' => $line, 'occurred_at' => '2026-11-22 02:00:00.000']]],
        ]],
    ];
}

function push(object $node, array $documents): TestResponse
{
    return test()->withToken($node->token)->postJson('/api/v1/sync/push', ['documents' => $documents]);
}

function voidDoc(object $node, string $sale, string $cashier, int $outbox): array
{
    $reason = (string) (DB::table('reasons')->where('type', 'SALE_VOID')->whereNull('deleted_at')->value('id')
        ?? app(MasterDataWriter::class)->create('reasons', ['type' => 'SALE_VOID', 'name' => 'Salah input'], WriteContext::system()));
    $id = id7();

    return [
        'outbox_id' => $outbox, 'doc_type' => 'SALE_VOID', 'doc_id' => $id, 'row_version' => 1,
        'occurred_at' => now('UTC')->format('Y-m-d H:i:s.v'), 'app_version' => '1.0.0',
        'payload' => ['format' => 2, 'node_id' => $node->id, 'tables' => [
            ['table' => 'sale_voids', 'rows' => [['id' => $id, 'node_id' => $node->id, 'sale_id' => $sale, 'reason_id' => $reason,
                'voided_at' => now('UTC')->format('Y-m-d H:i:s.v'), 'created_by' => $cashier]]],
        ]],
    ];
}
