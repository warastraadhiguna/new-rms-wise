<?php

namespace App\Http\Controllers\MasterData;

use App\Http\Controllers\Controller;
use App\Support\Catalog\ProductCatalog;
use App\Support\Catalog\ProductUsage;
use App\Support\Decimal;
use App\Support\MasterData\MasterDataWriter;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\Rule;
use Illuminate\Validation\ValidationException;
use Inertia\Inertia;
use Inertia\Response;

/**
 * Master Data → Produk: list (search by SKU, name or barcode; filter by category, brand, status; paged), create and
 * edit. Creating a product also creates its base unit (conversion 1, the default sale and purchase unit), the base
 * unit's barcodes and optionally its first default price. The base unit cannot change afterwards (stock is kept in it).
 */
class ProductController extends Controller
{
    public const STATUSES = ['ACTIVE', 'INACTIVE'];

    public function index(Request $request, ProductCatalog $catalog): Response
    {
        $filters = [
            'q' => trim((string) $request->query('q', '')),
            'category_id' => (string) $request->query('category_id', ''),
            'brand_id' => (string) $request->query('brand_id', ''),
            'status' => (string) $request->query('status', ''),
        ];

        return Inertia::render('master-data/products/index', [
            'filters' => $filters,
            'items' => $catalog->list($filters),
            ...$catalog->lookups(),
        ]);
    }

    public function create(ProductCatalog $catalog): Response
    {
        return Inertia::render('master-data/products/form', ['product' => null, ...$catalog->lookups()]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $data = $this->validated($request);
        $extra = $request->validate([
            'barcodes' => ['array', 'max:20'],
            'barcodes.*' => ['string', 'max:50', 'distinct'],
            'price' => ['nullable', 'numeric', 'decimal:0,4', 'gte:0'],
        ]);

        $id = DB::transaction(function () use ($writer, $data, $extra) {
            $productId = $writer->create('products', $data);
            $unitId = $writer->create('product_units', [
                'product_id' => $productId, 'unit_id' => $data['base_unit_id'], 'conversion' => '1.0000',
                'is_default_sale' => true, 'is_default_purchase' => true, 'sort_order' => 0,
            ]);
            foreach ($extra['barcodes'] ?? [] as $barcode) {
                ProductCatalog::addBarcode($writer, $unitId, (string) $barcode);
            }
            if (($extra['price'] ?? null) !== null && $extra['price'] !== '') {
                ProductCatalog::addPrice($writer, $unitId, (string) $extra['price'], '1', ProductCatalog::today(), null);
            }

            return $productId;
        });

        return $this->saved('Barang disimpan.', "/master-data/products/{$id}/edit");
    }

    public function edit(string $id, ProductCatalog $catalog): Response
    {
        return Inertia::render('master-data/products/form', [
            'product' => $catalog->detail($this->active($id)),
            ...$catalog->lookups(),
        ]);
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $id = $this->active($id);
        $data = $this->validated($request, $id);
        if ($data['base_unit_id'] !== DB::table('products')->where('id', $id)->value('base_unit_id')) {
            throw ValidationException::withMessages(['base_unit_id' => 'Satuan dasar tidak bisa diubah setelah barang dibuat (stok disimpan dalam satuan dasar).']);
        }
        $writer->update('products', $id, $data);

        return $this->saved('Barang diperbarui.');
    }

    /** Soft delete of the product with its units and barcodes (prices stay as history). */
    /**
     * Only a product that was never used may be deleted (with its units and barcodes; prices stay). A product with
     * any transaction, document or stock is refused with an offer to make it Nonaktif instead.
     */
    public function destroy(string $id, MasterDataWriter $writer, ProductUsage $usage): RedirectResponse
    {
        $id = $this->active($id);
        $usedIn = $usage->usedIn($id);
        if ($usedIn !== []) {
            throw ValidationException::withMessages([
                'delete' => 'Barang ini sudah pernah dipakai ('.implode(', ', $usedIn).') sehingga tidak bisa dihapus. Jadikan Nonaktif agar tidak dijual lagi.',
                'delete_used' => '1',
            ]);
        }
        DB::transaction(function () use ($writer, $id) {
            $units = DB::table('product_units')->where('product_id', $id)->whereNull('deleted_at')->pluck('id');
            foreach (DB::table('product_barcodes')->whereIn('product_unit_id', $units)->whereNull('deleted_at')->pluck('id') as $barcode) {
                $writer->delete('product_barcodes', (string) $barcode);
            }
            foreach ($units as $unit) {
                $writer->delete('product_units', (string) $unit);
            }
            $writer->delete('products', $id);
        });

        return $this->saved('Barang dihapus.', '/master-data/products');
    }

    /** "Jadikan Nonaktif": the alternative to deleting a used product. */
    public function deactivate(string $id, MasterDataWriter $writer): RedirectResponse
    {
        $id = $this->active($id);
        if (DB::table('products')->where('id', $id)->value('status') !== 'INACTIVE') {
            $writer->update('products', $id, ['status' => 'INACTIVE']);
        }

        return $this->saved('Barang dinonaktifkan.');
    }

    /** @return array<string, mixed> product columns */
    private function validated(Request $request, ?string $id = null): array
    {
        $data = $request->validate([
            'sku' => ['required', 'string', 'max:40', Rule::unique('products', 'sku')->ignore($id)],
            'name' => ['required', 'string', 'max:200'],
            'category_id' => ['nullable', 'string', Rule::exists('categories', 'id')->whereNull('deleted_at')],
            'brand_id' => ['nullable', 'string', Rule::exists('brands', 'id')->whereNull('deleted_at')],
            'base_unit_id' => ['required', 'string', Rule::exists('units', 'id')->whereNull('deleted_at')],
            'shelf_location' => ['nullable', 'string', 'max:40'],
            'track_expiry' => ['boolean'],
            'allow_qty_multiplier' => ['boolean'],
            'min_stock' => ['nullable', 'numeric', 'decimal:0,4', 'gte:0'],
            'status' => ['required', Rule::in(self::STATUSES)],
        ], ['sku.unique' => 'SKU sudah dipakai (termasuk barang yang sudah dihapus).']);

        $minStock = $data['min_stock'] ?? null;

        return [
            'sku' => trim((string) $data['sku']),
            'name' => trim((string) $data['name']),
            'category_id' => ($data['category_id'] ?? '') === '' ? null : (string) $data['category_id'],
            'brand_id' => ($data['brand_id'] ?? '') === '' ? null : (string) $data['brand_id'],
            'base_unit_id' => (string) $data['base_unit_id'],
            'shelf_location' => trim((string) ($data['shelf_location'] ?? '')) === '' ? null : trim((string) $data['shelf_location']),
            'track_expiry' => (bool) ($data['track_expiry'] ?? false),
            'allow_qty_multiplier' => (bool) ($data['allow_qty_multiplier'] ?? true),
            'min_stock' => $minStock === null || $minStock === '' ? null : Decimal::toString((string) $minStock),
            'status' => (string) $data['status'],
        ];
    }

    private function active(string $id): string
    {
        abort_unless(DB::table('products')->where('id', $id)->whereNull('deleted_at')->exists(), 404);

        return $id;
    }
}
