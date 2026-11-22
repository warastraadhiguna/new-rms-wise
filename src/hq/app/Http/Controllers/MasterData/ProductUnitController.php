<?php

namespace App\Http\Controllers\MasterData;

use App\Http\Controllers\Controller;
use App\Support\Catalog\ProductCatalog;
use App\Support\Decimal;
use App\Support\MasterData\MasterDataWriter;
use Carbon\CarbonImmutable;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\Rule;
use Illuminate\Validation\ValidationException;

/**
 * Selling units of a product (conversion to the base unit, barcodes) and their prices. The base unit always has
 * conversion 1 and cannot be removed. Prices are only added (new row per change) — a price that is not yet in effect
 * can be withdrawn; one in effect stays as history.
 */
class ProductUnitController extends Controller
{
    public function store(Request $request, string $product, MasterDataWriter $writer): RedirectResponse
    {
        $baseUnit = $this->baseUnit($product);
        $data = $request->validate([
            'unit_id' => ['required', 'string', Rule::exists('units', 'id')->whereNull('deleted_at')],
            'conversion' => ['required', 'numeric', 'decimal:0,4', 'gt:0'],
            'is_default_sale' => ['boolean'],
            'barcodes' => ['array', 'max:20'],
            'barcodes.*' => ['string', 'max:50', 'distinct'],
        ]);
        $unitId = (string) $data['unit_id'];
        if ($unitId === $baseUnit) {
            throw ValidationException::withMessages(['unit_id' => 'Satuan dasar sudah ada pada barang ini.']);
        }
        if (DB::table('product_units')->where('product_id', $product)->where('unit_id', $unitId)->whereNull('deleted_at')->exists()) {
            throw ValidationException::withMessages(['unit_id' => 'Satuan ini sudah ada pada barang ini.']);
        }

        DB::transaction(function () use ($writer, $product, $unitId, $data) {
            $values = ['conversion' => Decimal::toString((string) $data['conversion']), 'is_default_sale' => (bool) ($data['is_default_sale'] ?? false)];
            // The unique key (product, unit) keeps a removed unit: bring it back instead of a new row.
            $deleted = DB::table('product_units')->where('product_id', $product)->where('unit_id', $unitId)->value('id');
            if (is_string($deleted)) {
                $writer->restore('product_units', $deleted, $values);
                $id = $deleted;
            } else {
                $id = $writer->create('product_units', ['product_id' => $product, 'unit_id' => $unitId] + $values
                    + ['sort_order' => (int) DB::table('product_units')->where('product_id', $product)->max('sort_order') + 1]);
            }
            foreach ($data['barcodes'] ?? [] as $barcode) {
                ProductCatalog::addBarcode($writer, $id, (string) $barcode);
            }
            if ($values['is_default_sale']) {
                $this->onlyDefaultSale($writer, $product, $id);
            }
        });

        return $this->saved('Satuan jual ditambahkan.');
    }

    public function update(Request $request, string $product, string $unit, MasterDataWriter $writer): RedirectResponse
    {
        $baseUnit = $this->baseUnit($product);
        $unitOfRow = $this->unit($product, $unit);
        $data = $request->validate([
            'conversion' => ['required', 'numeric', 'decimal:0,4', 'gt:0'],
            'is_default_sale' => ['boolean'],
        ]);
        $conversion = Decimal::toString((string) $data['conversion']);
        if ($unitOfRow === $baseUnit && Decimal::compare($conversion, '1') !== 0) {
            throw ValidationException::withMessages(['conversion' => 'Satuan dasar wajib punya konversi 1.']);
        }

        DB::transaction(function () use ($writer, $product, $unit, $conversion, $data) {
            $writer->update('product_units', $unit, ['conversion' => $conversion, 'is_default_sale' => (bool) ($data['is_default_sale'] ?? false)]);
            if ((bool) ($data['is_default_sale'] ?? false)) {
                $this->onlyDefaultSale($writer, $product, $unit);
            }
        });

        return $this->saved('Satuan jual diperbarui.');
    }

    public function destroy(string $product, string $unit, MasterDataWriter $writer): RedirectResponse
    {
        $unitOfRow = $this->unit($product, $unit);
        if ($unitOfRow === $this->baseUnit($product)) {
            throw ValidationException::withMessages(['delete' => 'Satuan dasar tidak bisa dihapus.']);
        }
        DB::transaction(function () use ($writer, $unit) {
            foreach (DB::table('product_barcodes')->where('product_unit_id', $unit)->whereNull('deleted_at')->pluck('id') as $barcode) {
                $writer->delete('product_barcodes', (string) $barcode);
            }
            $writer->delete('product_units', $unit);
        });

        return $this->saved('Satuan jual dihapus.');
    }

    public function storeBarcode(Request $request, string $product, string $unit, MasterDataWriter $writer): RedirectResponse
    {
        $this->unit($product, $unit);
        $data = $request->validate(['barcode' => ['required', 'string', 'max:50']]);
        ProductCatalog::addBarcode($writer, $unit, (string) $data['barcode']);

        return $this->saved('Barcode ditambahkan.');
    }

    public function destroyBarcode(string $product, string $barcode, MasterDataWriter $writer): RedirectResponse
    {
        $exists = DB::table('product_barcodes as pb')->join('product_units as pu', 'pu.id', '=', 'pb.product_unit_id')
            ->where('pb.id', $barcode)->where('pu.product_id', $product)->whereNull('pb.deleted_at')->exists();
        abort_unless($exists, 404);
        $writer->delete('product_barcodes', $barcode);

        return $this->saved('Barcode dihapus.');
    }

    public function storePrice(Request $request, string $product, string $unit, MasterDataWriter $writer): RedirectResponse
    {
        $this->unit($product, $unit);
        $data = $request->validate([
            'price' => ['required', 'numeric', 'decimal:0,4', 'gte:0'],
            'min_qty' => ['required', 'numeric', 'decimal:0,4', 'gt:0'],
            'effective_from' => ['required', 'date_format:Y-m-d'],
            'branch_id' => ['nullable', 'string', Rule::exists('branches', 'id')->whereNull('deleted_at')],
        ]);
        $branch = ($data['branch_id'] ?? '') === '' ? null : (string) $data['branch_id'];
        ProductCatalog::addPrice($writer, $unit, (string) $data['price'], (string) $data['min_qty'], (string) $data['effective_from'], $branch);

        return $this->saved('Harga baru disimpan.');
    }

    /** Withdraw a price that is not in effect yet (a typo); prices in effect stay as history. */
    public function destroyPrice(string $product, string $price, MasterDataWriter $writer): RedirectResponse
    {
        $row = DB::table('prices as pr')->join('product_units as pu', 'pu.id', '=', 'pr.product_unit_id')
            ->where('pr.id', $price)->where('pu.product_id', $product)->whereNull('pr.deleted_at')->first(['pr.effective_from']);
        abort_unless($row !== null, 404);
        if (! CarbonImmutable::parse((string) $row->effective_from, 'UTC')->isFuture()) {
            throw ValidationException::withMessages(['delete' => 'Harga yang sudah berlaku tidak bisa dihapus; buat harga baru.']);
        }
        $writer->delete('prices', $price);

        return $this->saved('Harga yang belum berlaku dibatalkan.');
    }

    private function onlyDefaultSale(MasterDataWriter $writer, string $product, string $keep): void
    {
        $others = DB::table('product_units')->where('product_id', $product)->where('id', '<>', $keep)
            ->where('is_default_sale', true)->whereNull('deleted_at')->pluck('id');
        foreach ($others as $other) {
            $writer->update('product_units', (string) $other, ['is_default_sale' => false]);
        }
    }

    private function baseUnit(string $product): string
    {
        $base = DB::table('products')->where('id', $product)->whereNull('deleted_at')->value('base_unit_id');
        abort_unless(is_string($base), 404);

        return $base;
    }

    /** The unit (units.id) of an active selling unit of the product; 404 otherwise. */
    private function unit(string $product, string $unit): string
    {
        $unitId = DB::table('product_units')->where('id', $unit)->where('product_id', $product)->whereNull('deleted_at')->value('unit_id');
        abort_unless(is_string($unitId), 404);

        return $unitId;
    }
}
