<?php

namespace App\Support\Catalog;

use App\Support\Decimal;
use App\Support\MasterData\MasterDataWriter;
use App\Support\Settings;
use Carbon\CarbonImmutable;
use Illuminate\Contracts\Pagination\LengthAwarePaginator;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\ValidationException;

/**
 * Reads and helpers of the product master data screens. Prices are never overwritten: a new price is a new row
 * with its own effective_from; per unit, branch and min_qty the row with the latest effective_from not in the future
 * is the current one ("Berlaku"), later rows are "Akan berlaku" and earlier rows the history ("Riwayat").
 */
final class ProductCatalog
{
    public const PRICE_CURRENT = 'current';

    public const PRICE_FUTURE = 'future';

    public const PRICE_HISTORY = 'history';

    /**
     * @param  array{q: string, category_id: string, brand_id: string, status: string}  $filters
     * @return LengthAwarePaginator<int, object>
     */
    public function list(array $filters): LengthAwarePaginator
    {
        $q = $filters['q'];

        return DB::table('products as p')
            ->join('units as u', 'u.id', '=', 'p.base_unit_id')
            ->leftJoin('categories as c', 'c.id', '=', 'p.category_id')
            ->leftJoin('brands as b', 'b.id', '=', 'p.brand_id')
            ->whereNull('p.deleted_at')
            ->when($q !== '', fn ($query) => $query->where(fn ($w) => $w
                ->where('p.sku', 'like', "%{$q}%")
                ->orWhere('p.name', 'like', "%{$q}%")
                ->orWhereExists(fn ($e) => $e->selectRaw('1')->from('product_barcodes as pb')
                    ->join('product_units as pu', 'pu.id', '=', 'pb.product_unit_id')
                    ->whereColumn('pu.product_id', 'p.id')->whereNull('pb.deleted_at')->where('pb.barcode', $q))))
            ->when($filters['category_id'] !== '', fn ($query) => $query->where('p.category_id', $filters['category_id']))
            ->when($filters['brand_id'] !== '', fn ($query) => $query->where('p.brand_id', $filters['brand_id']))
            ->when($filters['status'] !== '', fn ($query) => $query->where('p.status', $filters['status']))
            ->orderBy('p.name')
            ->select(['p.id', 'p.sku', 'p.name', 'p.status', 'p.version', 'u.code as base_unit', 'c.name as category', 'b.name as brand'])
            ->selectRaw(
                <<<'SQL'
                (SELECT pr.price FROM prices pr JOIN product_units bpu ON bpu.id = pr.product_unit_id
                 WHERE bpu.product_id = p.id AND bpu.unit_id = p.base_unit_id AND bpu.deleted_at IS NULL
                   AND pr.branch_id IS NULL AND pr.min_qty = 1 AND pr.deleted_at IS NULL AND pr.effective_from <= UTC_TIMESTAMP(3)
                 ORDER BY pr.effective_from DESC, pr.price LIMIT 1) AS price
                SQL)
            ->paginate(25)
            ->withQueryString();
    }

    /** @return array<string, mixed> drop-down data of the product screens */
    public function lookups(): array
    {
        return [
            'units' => DB::table('units')->whereNull('deleted_at')->orderBy('code')->get(['id', 'code', 'name']),
            'categories' => DB::table('categories as c')->leftJoin('categories as p', 'p.id', '=', 'c.parent_id')
                ->whereNull('c.deleted_at')->orderByRaw('COALESCE(p.name, c.name), c.parent_id IS NOT NULL, c.name')
                ->get(['c.id', DB::raw("CASE WHEN p.name IS NULL THEN c.name ELSE CONCAT(p.name, ' / ', c.name) END AS name")]),
            'brands' => DB::table('brands')->whereNull('deleted_at')->orderBy('name')->get(['id', 'name']),
            'branches' => DB::table('branches')->whereNull('deleted_at')->orderBy('code')->get(['id', 'code', 'name']),
            'pricingUniform' => Settings::pricingUniform(),
            'statuses' => [['value' => 'ACTIVE', 'label' => 'Aktif'], ['value' => 'INACTIVE', 'label' => 'Nonaktif']],
        ];
    }

    /** @return array<string, mixed> a product with its units, barcodes and price history */
    public function detail(string $id): array
    {
        $product = (array) DB::table('products')->where('id', $id)->first([
            'id', 'sku', 'name', 'category_id', 'brand_id', 'base_unit_id', 'shelf_location', 'track_expiry',
            'allow_qty_multiplier', 'min_stock', 'status', 'version',
        ]);
        $units = DB::table('product_units as pu')->join('units as u', 'u.id', '=', 'pu.unit_id')
            ->where('pu.product_id', $id)->whereNull('pu.deleted_at')
            ->orderByRaw('pu.unit_id = ? DESC, pu.conversion', [$product['base_unit_id']])
            ->get(['pu.id', 'pu.unit_id', 'u.code as unit_code', 'u.name as unit_name', 'pu.conversion', 'pu.is_default_sale', 'pu.version']);
        $unitIds = $units->pluck('id');
        $barcodes = DB::table('product_barcodes')->whereIn('product_unit_id', $unitIds)->whereNull('deleted_at')
            ->orderBy('barcode')->get(['id', 'product_unit_id', 'barcode'])->groupBy('product_unit_id');
        $prices = DB::table('prices as pr')->leftJoin('branches as br', 'br.id', '=', 'pr.branch_id')
            ->leftJoin('users as us', 'us.id', '=', 'pr.created_by')
            ->whereIn('pr.product_unit_id', $unitIds)->whereNull('pr.deleted_at')
            ->orderByDesc('pr.effective_from')->orderBy('pr.min_qty')
            ->get(['pr.id', 'pr.product_unit_id', 'pr.branch_id', 'br.name as branch_name', 'pr.price', 'pr.min_qty',
                'pr.effective_from', 'us.full_name as created_by_name']);
        $statuses = self::priceStatuses(array_values($prices->map(fn (object $p) => PriceRow::from($p))->all()), CarbonImmutable::now('UTC'));
        $tz = (string) config('pos.display_timezone');

        $product['units'] = $units->map(fn (object $u) => [
            'id' => $u->id,
            'unit_id' => $u->unit_id,
            'unit_code' => $u->unit_code,
            'unit_name' => $u->unit_name,
            'conversion' => Decimal::toString((string) $u->conversion),
            'is_base' => $u->unit_id === $product['base_unit_id'],
            'is_default_sale' => (bool) $u->is_default_sale,
            'barcodes' => ($barcodes[$u->id] ?? collect())->map(fn ($b) => ['id' => $b->id, 'barcode' => $b->barcode])->values(),
            'prices' => $prices->where('product_unit_id', $u->id)->map(fn (object $p) => [
                'id' => $p->id,
                'branch_id' => $p->branch_id,
                'branch_name' => $p->branch_name,
                'price' => Decimal::toString((string) $p->price),
                'min_qty' => Decimal::toString((string) $p->min_qty),
                'effective_from' => CarbonImmutable::parse((string) $p->effective_from, 'UTC')->setTimezone($tz)->format('Y-m-d H:i'),
                'created_by_name' => $p->created_by_name,
                'status' => $statuses[$p->id],
            ])->values(),
        ])->values();
        $product['min_stock'] = $product['min_stock'] === null ? null : Decimal::toString((string) $product['min_stock']);
        $product['track_expiry'] = (bool) $product['track_expiry'];
        $product['allow_qty_multiplier'] = (bool) $product['allow_qty_multiplier'];

        return $product;
    }

    /**
     * current / future / history per price row, grouped by unit, branch and min_qty.
     *
     * @param  list<PriceRow>  $prices
     * @return array<string, string>
     */
    public static function priceStatuses(array $prices, CarbonImmutable $now): array
    {
        $groups = [];
        foreach ($prices as $p) {
            $groups[$p->productUnitId.'|'.($p->branchId ?? '').'|'.Decimal::toString($p->minQty)][] = $p;
        }
        $statuses = [];
        foreach ($groups as $rows) {
            usort($rows, fn (PriceRow $a, PriceRow $b) => strcmp($b->effectiveFrom, $a->effectiveFrom));
            $currentFound = false;
            foreach ($rows as $p) {
                if (CarbonImmutable::parse($p->effectiveFrom, 'UTC')->greaterThan($now)) {
                    $statuses[$p->id] = self::PRICE_FUTURE;
                } elseif (! $currentFound) {
                    $statuses[$p->id] = self::PRICE_CURRENT;
                    $currentFound = true;
                } else {
                    $statuses[$p->id] = self::PRICE_HISTORY;
                }
            }
        }

        return $statuses;
    }

    /**
     * Gives a barcode to a product unit: unique among active barcodes; a deleted row with the same barcode (the unique
     * key keeps it) is restored for this unit.
     */
    public static function addBarcode(MasterDataWriter $writer, string $productUnitId, string $barcode): string
    {
        $barcode = trim($barcode);
        if ($barcode === '' || mb_strlen($barcode) > 50) {
            throw ValidationException::withMessages(['barcode' => 'Barcode wajib diisi (maksimal 50 karakter).']);
        }
        $existing = DB::table('product_barcodes')->where('barcode', $barcode)->first(['id', 'deleted_at']);
        if ($existing !== null && $existing->deleted_at === null) {
            throw ValidationException::withMessages(['barcode' => "Barcode {$barcode} sudah dipakai barang lain."]);
        }
        if ($existing !== null) {
            $writer->restore('product_barcodes', (string) $existing->id, ['product_unit_id' => $productUnitId]);

            return (string) $existing->id;
        }

        return $writer->create('product_barcodes', ['product_unit_id' => $productUnitId, 'barcode' => $barcode]);
    }

    /**
     * A new price row (never an update): price >= 0, min_qty > 0, from the start of the given branch-local day.
     * Branch prices only while pricing.uniform is false.
     */
    public static function addPrice(MasterDataWriter $writer, string $productUnitId, string $price, string $minQty, string $effectiveDate, ?string $branchId): string
    {
        if ($branchId !== null && Settings::pricingUniform()) {
            throw ValidationException::withMessages(['branch_id' => 'Harga per cabang tidak dipakai: setting harga seragam (pricing.uniform) aktif.']);
        }
        if (Decimal::compare($price, '0') < 0) {
            throw ValidationException::withMessages(['price' => 'Harga tidak boleh negatif.']);
        }
        if (Decimal::compare($minQty, '0') <= 0) {
            throw ValidationException::withMessages(['min_qty' => 'Qty minimum harus lebih dari 0.']);
        }
        $tz = (string) config('pos.display_timezone');
        $date = preg_match('/^\d{4}-\d{2}-\d{2}$/', $effectiveDate) ? CarbonImmutable::createFromFormat('!Y-m-d', $effectiveDate, $tz) : null;
        if ($date === null) {
            throw ValidationException::withMessages(['effective_from' => 'Tanggal berlaku tidak valid.']);
        }
        if ($date->format('Y-m-d') < self::today()) {
            throw ValidationException::withMessages(['effective_from' => 'Tanggal berlaku tidak boleh di masa lalu (riwayat harga tidak diubah).']);
        }
        // Today: from this moment, so the new row is the latest and wins over an earlier price of the same day.
        $effectiveFrom = $date->format('Y-m-d') === self::today() ? CarbonImmutable::now('UTC') : $date->startOfDay()->utc();

        return $writer->create('prices', [
            'product_unit_id' => $productUnitId,
            'branch_id' => $branchId,
            'price' => Decimal::toString($price),
            'min_qty' => Decimal::toString($minQty),
            'effective_from' => $effectiveFrom->format('Y-m-d H:i:s.v'),
            'created_by' => (string) auth()->id(),
        ]);
    }

    /** Today in the display timezone (default effective date of a new price). */
    public static function today(): string
    {
        return CarbonImmutable::now((string) config('pos.display_timezone'))->format('Y-m-d');
    }
}
