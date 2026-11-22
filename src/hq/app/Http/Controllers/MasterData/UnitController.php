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

/** Master Data → Satuan (PCS, BOX, KG …). A code is unique for ever (the database keeps deleted codes). */
class UnitController extends Controller
{
    public function index(Request $request): Response
    {
        $q = trim((string) $request->query('q', ''));

        return Inertia::render('master-data/units', [
            'q' => $q,
            'items' => DB::table('units as u')
                ->whereNull('u.deleted_at')
                ->when($q !== '', fn ($query) => $query->where(fn ($w) => $w->where('u.code', 'like', "%{$q}%")->orWhere('u.name', 'like', "%{$q}%")))
                ->orderBy('u.code')
                ->select(['u.id', 'u.code', 'u.name', 'u.allow_decimal', 'u.version'])
                ->paginate(25)
                ->withQueryString(),
        ]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $writer->create('units', $this->validated($request));

        return $this->saved('Satuan disimpan.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $writer->update('units', $this->active($id), $this->validated($request, $id));

        return $this->saved('Satuan diperbarui.');
    }

    public function destroy(string $id, MasterDataWriter $writer): RedirectResponse
    {
        $id = $this->active($id);
        $used = DB::table('products')->where('base_unit_id', $id)->whereNull('deleted_at')->exists()
            || DB::table('product_units')->where('unit_id', $id)->whereNull('deleted_at')->exists();
        if ($used) {
            throw ValidationException::withMessages(['delete' => 'Satuan masih dipakai barang; tidak bisa dihapus.']);
        }
        $writer->delete('units', $id);

        return $this->saved('Satuan dihapus.');
    }

    /** @return array{code: string, name: string, allow_decimal: bool} */
    private function validated(Request $request, ?string $id = null): array
    {
        $data = $request->validate([
            'code' => ['required', 'string', 'max:20', 'regex:/^[A-Za-z0-9_-]+$/', Rule::unique('units', 'code')->ignore($id)],
            'name' => ['required', 'string', 'max:50'],
            'allow_decimal' => ['boolean'],
        ], ['code.unique' => 'Kode satuan sudah dipakai (termasuk satuan yang sudah dihapus).']);

        return [
            'code' => strtoupper((string) $data['code']),
            'name' => trim((string) $data['name']),
            'allow_decimal' => (bool) ($data['allow_decimal'] ?? false),
        ];
    }

    private function active(string $id): string
    {
        abort_unless(DB::table('units')->where('id', $id)->whereNull('deleted_at')->exists(), 404);

        return $id;
    }
}
