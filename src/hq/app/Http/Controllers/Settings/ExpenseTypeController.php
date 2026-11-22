<?php

namespace App\Http\Controllers\Settings;

use App\Http\Controllers\Controller;
use App\Support\MasterData\MasterDataWriter;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\ValidationException;
use Inertia\Inertia;
use Inertia\Response;

/** Jenis Biaya (cash expenses at a store): name unique among rows not deleted, optional GL account; soft delete. */
class ExpenseTypeController extends Controller
{
    public function index(): Response
    {
        return Inertia::render('settings/expense-types', [
            'items' => DB::table('expense_types')->whereNull('deleted_at')->orderBy('name')->get(['id', 'name', 'account_code', 'version']),
        ]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $writer->create('expense_types', $this->validated($request));

        return $this->saved('Jenis biaya disimpan.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('expense_types')->where('id', $id)->whereNull('deleted_at')->exists(), 404);
        $writer->update('expense_types', $id, $this->validated($request, $id));

        return $this->saved('Jenis biaya diperbarui.');
    }

    public function destroy(string $id, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('expense_types')->where('id', $id)->whereNull('deleted_at')->exists(), 404);
        $writer->delete('expense_types', $id);

        return $this->saved('Jenis biaya dihapus.');
    }

    /** @return array{name: string, account_code: ?string} */
    private function validated(Request $request, ?string $id = null): array
    {
        $data = $request->validate([
            'name' => ['required', 'string', 'max:200'],
            'account_code' => ['nullable', 'string', 'max:20'],
        ]);
        $name = trim((string) $data['name']);
        $taken = DB::table('expense_types')->where('name', $name)->whereNull('deleted_at')
            ->when($id !== null, fn ($q) => $q->where('id', '!=', $id))->exists();
        if ($taken) {
            throw ValidationException::withMessages(['name' => 'Jenis biaya ini sudah ada.']);
        }

        return ['name' => $name, 'account_code' => trim((string) ($data['account_code'] ?? '')) === '' ? null : trim((string) $data['account_code'])];
    }
}
