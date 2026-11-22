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

/** Master Data → Kategori, optionally under a main category (one level). Names are unique per parent (not deleted). */
class CategoryController extends Controller
{
    public function index(Request $request): Response
    {
        $q = trim((string) $request->query('q', ''));

        return Inertia::render('master-data/categories', [
            'q' => $q,
            'items' => DB::table('categories as c')
                ->leftJoin('categories as p', 'p.id', '=', 'c.parent_id')
                ->whereNull('c.deleted_at')
                ->when($q !== '', fn ($query) => $query->where('c.name', 'like', "%{$q}%"))
                ->orderByRaw('COALESCE(p.name, c.name), c.parent_id IS NOT NULL, c.name')
                ->select(['c.id', 'c.name', 'c.parent_id', 'p.name as parent_name', 'c.version'])
                ->selectRaw('(SELECT COUNT(*) FROM products x WHERE x.category_id = c.id AND x.deleted_at IS NULL) AS product_count')
                ->paginate(25)
                ->withQueryString(),
            'parents' => DB::table('categories')->whereNull('deleted_at')->whereNull('parent_id')->orderBy('name')->get(['id', 'name']),
        ]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $writer->create('categories', $this->validated($request));

        return $this->saved('Kategori disimpan.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $id = $this->active($id);
        $data = $this->validated($request, $id);
        if ($data['parent_id'] === $id) {
            throw ValidationException::withMessages(['parent_id' => 'Kategori tidak boleh menjadi induk dirinya sendiri.']);
        }
        if ($data['parent_id'] !== null && DB::table('categories')->where('parent_id', $id)->whereNull('deleted_at')->exists()) {
            throw ValidationException::withMessages(['parent_id' => 'Kategori yang punya sub-kategori tidak bisa dijadikan sub-kategori.']);
        }
        $writer->update('categories', $id, $data);

        return $this->saved('Kategori diperbarui.');
    }

    public function destroy(string $id, MasterDataWriter $writer): RedirectResponse
    {
        $id = $this->active($id);
        if (DB::table('products')->where('category_id', $id)->whereNull('deleted_at')->exists()) {
            throw ValidationException::withMessages(['delete' => 'Kategori masih dipakai barang; tidak bisa dihapus.']);
        }
        if (DB::table('categories')->where('parent_id', $id)->whereNull('deleted_at')->exists()) {
            throw ValidationException::withMessages(['delete' => 'Kategori masih punya sub-kategori; tidak bisa dihapus.']);
        }
        $writer->delete('categories', $id);

        return $this->saved('Kategori dihapus.');
    }

    /** @return array{name: string, parent_id: ?string} */
    private function validated(Request $request, ?string $id = null): array
    {
        $data = $request->validate([
            'name' => ['required', 'string', 'max:200'],
            'parent_id' => ['nullable', 'string', Rule::exists('categories', 'id')->whereNull('deleted_at')->whereNull('parent_id')],
        ]);
        $name = trim((string) $data['name']);
        $parent = isset($data['parent_id']) ? (string) $data['parent_id'] : null;
        $duplicate = DB::table('categories')->whereNull('deleted_at')->where('name', $name)
            ->where(fn ($q) => $parent === null ? $q->whereNull('parent_id') : $q->where('parent_id', $parent))
            ->when($id !== null, fn ($q) => $q->where('id', '<>', $id))
            ->exists();
        if ($duplicate) {
            throw ValidationException::withMessages(['name' => 'Nama kategori sudah ada.']);
        }

        return ['name' => $name, 'parent_id' => $parent];
    }

    private function active(string $id): string
    {
        abort_unless(DB::table('categories')->where('id', $id)->whereNull('deleted_at')->exists(), 404);

        return $id;
    }
}
