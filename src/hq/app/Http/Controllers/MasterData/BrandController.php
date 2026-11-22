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

/** Master Data → Merek. A name is unique among brands that are not deleted. */
class BrandController extends Controller
{
    public function index(Request $request): Response
    {
        $q = trim((string) $request->query('q', ''));

        return Inertia::render('master-data/brands', [
            'q' => $q,
            'items' => DB::table('brands as b')
                ->whereNull('b.deleted_at')
                ->when($q !== '', fn ($query) => $query->where('b.name', 'like', "%{$q}%"))
                ->orderBy('b.name')
                ->select(['b.id', 'b.name', 'b.version'])
                ->selectRaw('(SELECT COUNT(*) FROM products p WHERE p.brand_id = b.id AND p.deleted_at IS NULL) AS product_count')
                ->paginate(25)
                ->withQueryString(),
        ]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $writer->create('brands', $this->validated($request));

        return $this->saved('Merek disimpan.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $writer->update('brands', $this->active($id), $this->validated($request, $id));

        return $this->saved('Merek diperbarui.');
    }

    public function destroy(string $id, MasterDataWriter $writer): RedirectResponse
    {
        $id = $this->active($id);
        if (DB::table('products')->where('brand_id', $id)->whereNull('deleted_at')->exists()) {
            throw ValidationException::withMessages(['delete' => 'Merek masih dipakai barang; tidak bisa dihapus.']);
        }
        $writer->delete('brands', $id);

        return $this->saved('Merek dihapus.');
    }

    /** @return array{name: string} */
    private function validated(Request $request, ?string $id = null): array
    {
        $data = $request->validate([
            'name' => ['required', 'string', 'max:200', Rule::unique('brands', 'name')->whereNull('deleted_at')->ignore($id)],
        ], ['name.unique' => 'Nama merek sudah ada.']);

        return ['name' => trim((string) $data['name'])];
    }

    private function active(string $id): string
    {
        abort_unless(DB::table('brands')->where('id', $id)->whereNull('deleted_at')->exists(), 404);

        return $id;
    }
}
