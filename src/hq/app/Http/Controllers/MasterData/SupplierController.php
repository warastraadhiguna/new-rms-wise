<?php

namespace App\Http\Controllers\MasterData;

use App\Http\Controllers\Controller;
use App\Support\MasterData\MasterDataWriter;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\Rule;
use Illuminate\Validation\ValidationException;
use Inertia\Inertia;
use Inertia\Response;

/** A supplier's page: its sales reps (salesman) and the products it supplies (supplier_products). */
class SupplierController extends Controller
{
    public function show(string $id): Response
    {
        $supplier = $this->supplier($id);

        return Inertia::render('master-data/supplier', [
            'supplier' => $supplier,
            'reps' => DB::table('sales_reps')->where('supplier_id', $id)->whereNull('deleted_at')->orderBy('name')
                ->get(['id', 'name', 'phone', 'notes', 'is_active']),
            'products' => DB::table('supplier_products as sp')
                ->join('products as p', 'p.id', '=', 'sp.product_id')
                ->leftJoin('sales_reps as r', 'r.id', '=', 'sp.sales_rep_id')
                ->where('sp.supplier_id', $id)->whereNull('sp.deleted_at')
                ->orderBy('p.name')
                ->get(['sp.id', 'sp.product_id', 'p.sku', 'p.name', 'sp.supplier_sku', 'sp.sales_rep_id', 'r.name as sales_rep_name']),
        ]);
    }

    public function storeRep(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $this->supplier($id);
        $writer->create('sales_reps', ['supplier_id' => $id] + $this->rep($request));

        return $this->saved('Salesman disimpan.');
    }

    public function updateRep(Request $request, string $id, string $rep, MasterDataWriter $writer): RedirectResponse
    {
        $this->ownRep($id, $rep);
        $writer->update('sales_reps', $rep, $this->rep($request));

        return $this->saved('Salesman diperbarui.');
    }

    public function destroyRep(string $id, string $rep, MasterDataWriter $writer): RedirectResponse
    {
        $this->ownRep($id, $rep);
        DB::transaction(function () use ($writer, $rep) {
            foreach (DB::table('supplier_products')->where('sales_rep_id', $rep)->whereNull('deleted_at')->pluck('id') as $sp) {
                $writer->update('supplier_products', (string) $sp, ['sales_rep_id' => null]);
            }
            $writer->delete('sales_reps', $rep);
        });

        return $this->saved('Salesman dihapus.');
    }

    /** Adds a product by its SKU or one of its barcodes; a product removed earlier comes back (unique key). */
    public function storeProduct(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $this->supplier($id);
        $data = $request->validate([
            'product' => ['required', 'string', 'max:50'],
            'supplier_sku' => ['nullable', 'string', 'max:50'],
            'sales_rep_id' => ['nullable', 'string', Rule::exists('sales_reps', 'id')->where('supplier_id', $id)->whereNull('deleted_at')],
        ]);
        $code = trim((string) $data['product']);
        $productId = DB::table('products')->where('sku', $code)->whereNull('deleted_at')->value('id')
            ?? DB::table('product_barcodes as pb')->join('product_units as pu', 'pu.id', '=', 'pb.product_unit_id')
                ->join('products as p', 'p.id', '=', 'pu.product_id')
                ->where('pb.barcode', $code)->whereNull('pb.deleted_at')->whereNull('p.deleted_at')->value('p.id');
        if (! is_string($productId)) {
            throw ValidationException::withMessages(['product' => "Barang dengan SKU atau barcode {$code} tidak ditemukan."]);
        }
        $existing = DB::table('supplier_products')->where('supplier_id', $id)->where('product_id', $productId)->first(['id', 'deleted_at']);
        if ($existing !== null && $existing->deleted_at === null) {
            throw ValidationException::withMessages(['product' => 'Barang ini sudah ada di daftar supplier ini.']);
        }
        $values = [
            'supplier_sku' => trim((string) ($data['supplier_sku'] ?? '')) === '' ? null : trim((string) $data['supplier_sku']),
            'sales_rep_id' => ($data['sales_rep_id'] ?? '') === '' ? null : (string) $data['sales_rep_id'],
        ];
        $existing !== null
            ? $writer->restore('supplier_products', (string) $existing->id, $values)
            : $writer->create('supplier_products', ['supplier_id' => $id, 'product_id' => $productId] + $values);

        return $this->saved('Barang ditambahkan ke supplier.');
    }

    public function destroyProduct(string $id, string $item, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('supplier_products')->where('id', $item)->where('supplier_id', $id)->whereNull('deleted_at')->exists(), 404);
        $writer->delete('supplier_products', $item);

        return $this->saved('Barang dihapus dari supplier.');
    }

    /** @return array{name: string, phone: ?string, notes: ?string, is_active: bool} */
    private function rep(Request $request): array
    {
        $data = $request->validate([
            'name' => ['required', 'string', 'max:250'],
            'phone' => ['nullable', 'string', 'max:50'],
            'notes' => ['nullable', 'string', 'max:2000'],
            'is_active' => ['boolean'],
        ]);

        return [
            'name' => trim((string) $data['name']),
            'phone' => trim((string) ($data['phone'] ?? '')) === '' ? null : trim((string) $data['phone']),
            'notes' => trim((string) ($data['notes'] ?? '')) === '' ? null : trim((string) $data['notes']),
            'is_active' => (bool) ($data['is_active'] ?? true),
        ];
    }

    /** @return array<string, mixed> */
    private function supplier(string $id): array
    {
        $row = DB::table('contacts')->where('id', $id)->where('is_supplier', true)->whereNull('deleted_at')
            ->first(['id', 'code', 'name', 'company_name', 'phone', 'bank_account', 'is_active']);
        abort_unless($row !== null, 404);

        return (array) $row;
    }

    private function ownRep(string $supplier, string $rep): void
    {
        abort_unless(DB::table('sales_reps')->where('id', $rep)->where('supplier_id', $supplier)->whereNull('deleted_at')->exists(), 404);
    }
}
