<?php

namespace App\Http\Controllers\Settings;

use App\Http\Controllers\Controller;
use App\Support\MasterData\MasterDataWriter;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\Rule;
use Illuminate\Validation\ValidationException;
use Inertia\Inertia;
use Inertia\Response;

/**
 * Metode Bayar. Code unique for ever; a method is cash (counts in the drawer), credit (a receivable) or neither,
 * never both. Methods are not deleted (sales refer to them): Nonaktif. RETURN ("RETUR") is the system method that pays
 * a nota with a sale return (Tukar Barang): the page creates it when missing; its code, flags and active state are fixed,
 * only its name can change. The node never offers it in Form Bayar.
 */
class PaymentMethodController extends Controller
{
    public const RETURN = 'RETURN';

    public function index(): Response
    {
        return Inertia::render('settings/payment-methods', [
            'items' => DB::table('payment_methods')->whereNull('deleted_at')->orderBy('code')
                ->get(['id', 'code', 'name', 'is_cash', 'is_credit', 'account_code', 'is_active', 'version']),
            'returnCode' => self::RETURN,
        ]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $data = $this->validated($request);
        if ($data['code'] === self::RETURN) {
            $data = ['is_cash' => false, 'is_credit' => false, 'is_active' => true] + $data;
        }
        $writer->create('payment_methods', $data);

        return $this->saved('Metode bayar disimpan.');
    }

    /** Creates the RETURN method (RETUR) when it is missing. */
    public function storeReturn(MasterDataWriter $writer): RedirectResponse
    {
        if (! DB::table('payment_methods')->where('code', self::RETURN)->exists()) {
            $writer->create('payment_methods', ['code' => self::RETURN, 'name' => 'RETUR', 'is_cash' => false, 'is_credit' => false, 'is_active' => true]);
        }

        return $this->saved('Metode bayar RETUR tersedia.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $code = DB::table('payment_methods')->where('id', $id)->whereNull('deleted_at')->value('code');
        abort_unless(is_string($code), 404);
        $data = $this->validated($request, $id);
        if ($code === self::RETURN) {
            $data = ['code' => self::RETURN, 'is_cash' => false, 'is_credit' => false, 'is_active' => true] + $data;
        } elseif ($data['code'] === self::RETURN) {
            throw ValidationException::withMessages(['code' => 'Kode RETURN dipakai sistem.']);
        }
        $writer->update('payment_methods', $id, $data);

        return $this->saved('Metode bayar diperbarui.');
    }

    /** @return array{code: string, name: string, is_cash: bool, is_credit: bool, account_code: ?string, is_active: bool} */
    private function validated(Request $request, ?string $id = null): array
    {
        $request->merge(['code' => strtoupper(trim((string) $request->input('code', '')))]);
        $data = $request->validate([
            'code' => ['required', 'string', 'max:20', 'regex:/^[A-Z0-9_]+$/', Rule::unique('payment_methods', 'code')->ignore($id)],
            'name' => ['required', 'string', 'max:50'],
            'is_cash' => ['boolean'],
            'is_credit' => ['boolean'],
            'account_code' => ['nullable', 'string', 'max:20'],
            'is_active' => ['boolean'],
        ], [
            'code.unique' => 'Kode sudah dipakai (termasuk metode yang sudah dihapus).',
            'code.regex' => 'Kode hanya boleh huruf besar, angka, dan garis bawah.',
        ]);
        $cash = (bool) ($data['is_cash'] ?? false);
        $credit = (bool) ($data['is_credit'] ?? false);
        if ($cash && $credit) {
            throw ValidationException::withMessages(['is_credit' => 'Metode bayar tidak bisa tunai dan kredit sekaligus.']);
        }

        return [
            'code' => (string) $data['code'],
            'name' => trim((string) $data['name']),
            'is_cash' => $cash,
            'is_credit' => $credit,
            'account_code' => trim((string) ($data['account_code'] ?? '')) === '' ? null : trim((string) $data['account_code']),
            'is_active' => (bool) ($data['is_active'] ?? true),
        ];
    }
}
