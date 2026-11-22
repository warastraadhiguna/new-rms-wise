<?php

namespace App\Http\Controllers\MasterData;

use App\Http\Controllers\Controller;
use App\Support\Decimal;
use App\Support\MasterData\CodeGenerator;
use App\Support\MasterData\MasterDataWriter;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\Rule;
use Inertia\Inertia;
use Inertia\Response;

/**
 * Pelanggan and Supplier (table contacts; one contact may be both). A code left empty becomes the next C000001 /
 * S000001; codes are unique for ever. Contacts are not deleted (sales and purchases refer to them): Nonaktif instead.
 * Customers: credit limit (empty = no credit sales) and payment term in days. Suppliers: bank account; sales reps and
 * supplied products are on the supplier page (SupplierController).
 */
class ContactController extends Controller
{
    public const KINDS = ['customers' => ['flag' => 'is_customer', 'prefix' => 'C'], 'suppliers' => ['flag' => 'is_supplier', 'prefix' => 'S']];

    public function index(Request $request, string $kind): Response
    {
        $flag = self::KINDS[$kind]['flag'];
        $q = trim((string) $request->query('q', ''));
        $active = (string) $request->query('active', '');

        return Inertia::render('master-data/contacts', [
            'kind' => $kind,
            'q' => $q,
            'active' => $active,
            'items' => DB::table('contacts')
                ->where($flag, true)
                ->whereNull('deleted_at')
                ->when($q !== '', fn ($query) => $query->where(fn ($w) => $w->where('code', 'like', "%{$q}%")
                    ->orWhere('name', 'like', "%{$q}%")->orWhere('company_name', 'like', "%{$q}%")->orWhere('phone', 'like', "%{$q}%")))
                ->when($active !== '', fn ($query) => $query->where('is_active', $active === '1'))
                ->orderBy('name')
                ->select(['id', 'code', 'name', 'company_name', 'address', 'email', 'phone', 'tax_id', 'credit_limit',
                    'payment_term_days', 'bank_account', 'is_customer', 'is_supplier', 'is_active', 'version'])
                ->paginate(25)
                ->withQueryString(),
        ]);
    }

    public function store(Request $request, string $kind, MasterDataWriter $writer): RedirectResponse
    {
        $data = $this->validated($request, $kind);
        $data[self::KINDS[$kind]['flag']] = true;

        if ($data['code'] === null) {
            CodeGenerator::withNext('contacts', 'code', self::KINDS[$kind]['prefix'],
                fn (string $code) => $writer->create('contacts', ['code' => $code] + $data));
        } else {
            $writer->create('contacts', $data);
        }

        return $this->saved($kind === 'customers' ? 'Pelanggan disimpan.' : 'Supplier disimpan.');
    }

    public function update(Request $request, string $kind, string $id, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('contacts')->where('id', $id)->where(self::KINDS[$kind]['flag'], true)->whereNull('deleted_at')->exists(), 404);
        $data = $this->validated($request, $kind, $id);
        if ($data['code'] === null) {
            unset($data['code']);                      // an existing contact keeps its code
        }
        $writer->update('contacts', $id, $data);

        return $this->saved($kind === 'customers' ? 'Pelanggan diperbarui.' : 'Supplier diperbarui.');
    }

    /** @return array<string, mixed> */
    private function validated(Request $request, string $kind, ?string $id = null): array
    {
        $data = $request->validate([
            'code' => ['nullable', 'string', 'max:30', 'regex:/^[A-Za-z0-9_-]+$/', Rule::unique('contacts', 'code')->ignore($id)],
            'name' => ['required', 'string', 'max:300'],
            'company_name' => ['nullable', 'string', 'max:200'],
            'address' => ['nullable', 'string', 'max:2000'],
            'email' => ['nullable', 'email', 'max:200'],
            'phone' => ['nullable', 'string', 'max:50'],
            'tax_id' => ['nullable', 'string', 'max:30'],
            'is_active' => ['boolean'],
            'is_customer' => ['boolean'],
            'is_supplier' => ['boolean'],
            'credit_limit' => ['nullable', 'numeric', 'decimal:0,4', 'gte:0'],
            'payment_term_days' => ['nullable', 'integer', 'min:0', 'max:365'],
            'bank_account' => ['nullable', 'string', 'max:2000'],
        ], ['code.unique' => 'Kode sudah dipakai (termasuk kontak yang sudah dihapus).']);

        $text = fn (string $key): ?string => trim((string) ($data[$key] ?? '')) === '' ? null : trim((string) $data[$key]);
        $limit = $data['credit_limit'] ?? null;

        return [
            'code' => ($c = $text('code')) === null ? null : strtoupper($c),
            'name' => (string) $text('name'),
            'company_name' => $text('company_name'),
            'address' => $text('address'),
            'email' => $text('email'),
            'phone' => $text('phone'),
            'tax_id' => $text('tax_id'),
            'is_active' => (bool) ($data['is_active'] ?? true),
            'is_customer' => (bool) ($data['is_customer'] ?? $kind === 'customers'),
            'is_supplier' => (bool) ($data['is_supplier'] ?? $kind === 'suppliers'),
            'credit_limit' => $limit === null || $limit === '' ? null : Decimal::toString((string) $limit),
            'payment_term_days' => isset($data['payment_term_days']) && $data['payment_term_days'] !== '' ? (int) $data['payment_term_days'] : null,
            'bank_account' => $text('bank_account'),
        ];
    }
}
