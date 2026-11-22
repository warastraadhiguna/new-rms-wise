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
 * Alasan for voids, returns and stock adjustments (reasons.type), chosen from a list at the node. Names are unique per
 * type among rows not deleted; deleting is soft (documents keep pointing at the old row).
 */
class ReasonController extends Controller
{
    public const TYPES = ['SALE_VOID' => 'Void penjualan', 'RETURN' => 'Retur', 'ADJUSTMENT' => 'Penyesuaian stok'];

    public function index(): Response
    {
        return Inertia::render('settings/reasons', [
            'items' => DB::table('reasons')->whereNull('deleted_at')->orderBy('type')->orderBy('name')->get(['id', 'type', 'name', 'version']),
            'types' => self::TYPES,
        ]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $writer->create('reasons', $this->validated($request));

        return $this->saved('Alasan disimpan.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('reasons')->where('id', $id)->whereNull('deleted_at')->exists(), 404);
        $writer->update('reasons', $id, $this->validated($request, $id));

        return $this->saved('Alasan diperbarui.');
    }

    public function destroy(string $id, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('reasons')->where('id', $id)->whereNull('deleted_at')->exists(), 404);
        $writer->delete('reasons', $id);

        return $this->saved('Alasan dihapus.');
    }

    /** @return array{type: string, name: string} */
    private function validated(Request $request, ?string $id = null): array
    {
        $data = $request->validate([
            'type' => ['required', Rule::in(array_keys(self::TYPES))],
            'name' => ['required', 'string', 'max:100'],
        ]);
        $name = trim((string) $data['name']);
        $taken = DB::table('reasons')->where('type', $data['type'])->where('name', $name)->whereNull('deleted_at')
            ->when($id !== null, fn ($q) => $q->where('id', '!=', $id))->exists();
        if ($taken) {
            throw ValidationException::withMessages(['name' => 'Alasan ini sudah ada untuk jenis tersebut.']);
        }

        return ['type' => (string) $data['type'], 'name' => $name];
    }
}
