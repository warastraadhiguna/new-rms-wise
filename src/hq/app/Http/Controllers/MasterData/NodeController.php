<?php

namespace App\Http\Controllers\MasterData;

use App\Http\Controllers\Controller;
use App\Support\MasterData\MasterDataWriter;
use App\Sync\NodeTokens;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\Rule;
use Illuminate\Validation\ValidationException;
use Inertia\Inertia;
use Inertia\Response;

/**
 * Toko dan Gudang: nodes (code unique for ever, at most 10 characters; type STORE or WAREHOUSE — the HQ node comes
 * from setup) and their stock locations. A new store gets DISPLAY (sellable) and DAMAGED, a new warehouse STORAGE and
 * DAMAGED, in the same transaction. Nodes and locations are not deleted (stock and documents refer to them): Nonaktif.
 * The type of a node never changes after it is created.
 */
class NodeController extends Controller
{
    public const TYPES = ['STORE' => 'Toko', 'WAREHOUSE' => 'Gudang', 'HQ' => 'Pusat'];

    public const LOCATION_TYPES = ['DISPLAY' => 'Display (rak toko)', 'STORAGE' => 'Gudang / penyimpanan', 'DAMAGED' => 'Barang rusak'];

    /** Locations created with a new node of that type: code, name, type, sellable. */
    public const DEFAULT_LOCATIONS = [
        'STORE' => [['DISPLAY', 'Display', 'DISPLAY', true], ['DAMAGED', 'Barang Rusak', 'DAMAGED', false]],
        'WAREHOUSE' => [['STORAGE', 'Gudang', 'STORAGE', false], ['DAMAGED', 'Barang Rusak', 'DAMAGED', false]],
    ];

    public function index(Request $request): Response
    {
        $q = trim((string) $request->query('q', ''));

        return Inertia::render('master-data/nodes', [
            'q' => $q,
            'items' => DB::table('nodes as n')
                ->join('branches as b', 'b.id', '=', 'n.branch_id')
                ->whereNull('n.deleted_at')
                ->when($q !== '', fn ($query) => $query->where(fn ($w) => $w->where('n.code', 'like', "%{$q}%")
                    ->orWhere('n.name', 'like', "%{$q}%")->orWhere('n.address', 'like', "%{$q}%")))
                ->orderBy('n.code')
                ->select(['n.id', 'n.code', 'n.name', 'n.type', 'n.branch_id', 'b.name as branch_name', 'n.address', 'n.is_active', 'n.version'])
                ->selectSub(fn ($s) => $s->from('locations')->whereColumn('node_id', 'n.id')->whereNull('deleted_at')->selectRaw('COUNT(*)'), 'locations')
                ->paginate(25)
                ->withQueryString(),
            'branches' => DB::table('branches')->whereNull('deleted_at')->orderBy('code')->get(['id', 'code', 'name']),
            'types' => self::TYPES,
        ]);
    }

    public function show(string $id): Response
    {
        $node = DB::table('nodes as n')->join('branches as b', 'b.id', '=', 'n.branch_id')
            ->where('n.id', $id)->whereNull('n.deleted_at')
            ->first(['n.id', 'n.code', 'n.name', 'n.type', 'b.name as branch_name', 'n.address', 'n.is_active']);
        abort_unless($node !== null, 404);

        return Inertia::render('master-data/node', [
            'node' => $node,
            'locations' => DB::table('locations')->where('node_id', $id)->whereNull('deleted_at')->orderBy('code')
                ->get(['id', 'code', 'name', 'type', 'is_sellable', 'is_active', 'version']),
            'types' => self::TYPES,
            'locationTypes' => self::LOCATION_TYPES,
            'syncToken' => app(NodeTokens::class)->active($id),
        ]);
    }

    /** "Buat token sinkron": a new token for the node (the previous one stops working), shown once. */
    public function createToken(Request $request, string $id, NodeTokens $tokens): RedirectResponse
    {
        $type = DB::table('nodes')->where('id', $id)->whereNull('deleted_at')->value('type');
        abort_unless(is_string($type), 404);
        if ($type === 'HQ') {
            throw ValidationException::withMessages(['token' => 'Node pusat tidak memakai token sinkron.']);
        }
        $token = $tokens->create($id, (string) $request->user()?->getAuthIdentifier());
        Inertia::flash('sync_token', $token);

        return $this->saved('Token sinkron dibuat. Salin sekarang: token tidak ditampilkan lagi.');
    }

    public function revokeToken(Request $request, string $id, NodeTokens $tokens): RedirectResponse
    {
        abort_unless(DB::table('nodes')->where('id', $id)->whereNull('deleted_at')->exists(), 404);
        $tokens->revoke($id, (string) $request->user()?->getAuthIdentifier());

        return $this->saved('Token sinkron dicabut. Node ini tidak bisa sinkron sampai diberi token baru.');
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $data = $this->validated($request);
        $type = (string) $request->validate(['type' => ['required', Rule::in(array_keys(self::DEFAULT_LOCATIONS))]])['type'];

        DB::transaction(function () use ($writer, $data, $type) {
            $node = $writer->create('nodes', $data + ['type' => $type]);
            foreach (self::DEFAULT_LOCATIONS[$type] as [$code, $name, $locationType, $sellable]) {
                $writer->create('locations', ['node_id' => $node, 'code' => $code, 'name' => $name, 'type' => $locationType, 'is_sellable' => $sellable]);
            }
        });

        return $this->saved(($type === 'STORE' ? 'Toko' : 'Gudang').' disimpan dengan lokasi bawaan.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('nodes')->where('id', $id)->whereNull('deleted_at')->exists(), 404);
        $writer->update('nodes', $id, $this->validated($request, $id));

        return $this->saved('Data node diperbarui.');
    }

    public function storeLocation(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('nodes')->where('id', $id)->whereNull('deleted_at')->exists(), 404);
        $writer->create('locations', ['node_id' => $id] + $this->location($request, $id));

        return $this->saved('Lokasi disimpan.');
    }

    public function updateLocation(Request $request, string $id, string $location, MasterDataWriter $writer): RedirectResponse
    {
        abort_unless(DB::table('locations')->where('id', $location)->where('node_id', $id)->whereNull('deleted_at')->exists(), 404);
        $writer->update('locations', $location, $this->location($request, $id, $location));

        return $this->saved('Lokasi diperbarui.');
    }

    /** @return array{code: string, name: string, branch_id: string, address: ?string, is_active: bool} */
    private function validated(Request $request, ?string $id = null): array
    {
        $request->merge(['code' => strtoupper(trim((string) $request->input('code', '')))]);
        $data = $request->validate([
            'code' => ['required', 'string', 'max:10', 'regex:/^[A-Z0-9]+$/', Rule::unique('nodes', 'code')->ignore($id)],
            'name' => ['required', 'string', 'max:200'],
            'branch_id' => ['required', 'string', Rule::exists('branches', 'id')->whereNull('deleted_at')],
            'address' => ['nullable', 'string', 'max:2000'],
            'is_active' => ['boolean'],
        ], [
            'code.unique' => 'Kode sudah dipakai (termasuk node yang sudah dihapus).',
            'code.regex' => 'Kode hanya boleh huruf besar dan angka, misalnya TK02 atau GD01.',
        ]);

        return [
            'code' => (string) $data['code'],
            'name' => trim((string) $data['name']),
            'branch_id' => (string) $data['branch_id'],
            'address' => trim((string) ($data['address'] ?? '')) === '' ? null : trim((string) $data['address']),
            'is_active' => (bool) ($data['is_active'] ?? true),
        ];
    }

    /** @return array{code: string, name: string, type: string, is_sellable: bool, is_active: bool} */
    private function location(Request $request, string $node, ?string $id = null): array
    {
        $request->merge(['code' => strtoupper(trim((string) $request->input('code', '')))]);
        $data = $request->validate([
            'code' => ['required', 'string', 'max:20', 'regex:/^[A-Z0-9_-]+$/', Rule::unique('locations', 'code')->where('node_id', $node)->ignore($id)],
            'name' => ['required', 'string', 'max:100'],
            'type' => ['required', Rule::in(array_keys(self::LOCATION_TYPES))],
            'is_sellable' => ['boolean'],
            'is_active' => ['boolean'],
        ], [
            'code.unique' => 'Kode lokasi sudah dipakai di node ini.',
            'code.regex' => 'Kode hanya boleh huruf besar, angka, garis bawah, dan tanda minus.',
        ]);
        $sellable = (bool) ($data['is_sellable'] ?? false);
        if ($sellable && $data['type'] === 'DAMAGED') {
            throw ValidationException::withMessages(['is_sellable' => 'Lokasi barang rusak tidak boleh dijual.']);
        }

        return [
            'code' => (string) $data['code'],
            'name' => trim((string) $data['name']),
            'type' => (string) $data['type'],
            'is_sellable' => $sellable,
            'is_active' => (bool) ($data['is_active'] ?? true),
        ];
    }
}
