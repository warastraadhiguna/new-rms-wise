<?php

namespace App\Http\Controllers\Settings;

use App\Auth\OwnerGuard;
use App\Auth\PermissionCatalog;
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
 * Role: a code (unique for ever), a name and permissions ticked from PermissionCatalog, plus discount.max:N.
 * The OWNER role always has "*" and cannot be renamed (code) or deleted. A role still given to users cannot be deleted.
 */
class RoleController extends Controller
{
    public function index(): Response
    {
        $roles = DB::table('roles')->whereNull('deleted_at')->orderBy('name')
            ->get(['id', 'code', 'name', 'permissions', 'version'])
            ->map(fn (object $r) => [
                'id' => $r->id,
                'code' => $r->code,
                'name' => $r->name,
                'permissions' => $this->decode($r->permissions),
                'version' => (int) $r->version,
                'users' => DB::table('user_roles')->where('role_id', $r->id)->whereNull('deleted_at')->distinct()->count('user_id'),
            ]);

        return Inertia::render('settings/roles', [
            'roles' => $roles,
            'catalog' => PermissionCatalog::groups(),
            'ownerCode' => OwnerGuard::ROLE_CODE,
        ]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $data = $this->validated($request);
        $writer->create('roles', $data);

        return $this->saved('Role disimpan.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $code = DB::table('roles')->where('id', $id)->whereNull('deleted_at')->value('code');
        abort_unless(is_string($code), 404);
        $data = $this->validated($request, $id);
        if ($code === OwnerGuard::ROLE_CODE) {
            $data['code'] = OwnerGuard::ROLE_CODE;               // OWNER keeps its code and everything
            $data['permissions'] = json_encode([PermissionCatalog::ALL]);
        } elseif ($data['code'] === OwnerGuard::ROLE_CODE) {
            throw ValidationException::withMessages(['code' => 'Kode OWNER sudah dipakai.']);
        }
        $writer->update('roles', $id, $data);

        return $this->saved('Role diperbarui.');
    }

    public function destroy(string $id, MasterDataWriter $writer): RedirectResponse
    {
        $code = DB::table('roles')->where('id', $id)->whereNull('deleted_at')->value('code');
        abort_unless(is_string($code), 404);
        if ($code === OwnerGuard::ROLE_CODE) {
            throw ValidationException::withMessages(['delete' => 'Role OWNER tidak bisa dihapus.']);
        }
        if (DB::table('user_roles')->where('role_id', $id)->whereNull('deleted_at')->exists()) {
            throw ValidationException::withMessages(['delete' => 'Role ini masih diberikan ke pengguna. Cabut dulu dari semua pengguna.']);
        }
        $writer->delete('roles', $id);

        return $this->saved('Role dihapus.');
    }

    /** @return array{code: string, name: string, permissions: string} */
    private function validated(Request $request, ?string $id = null): array
    {
        $request->merge(['code' => strtoupper(trim((string) $request->input('code', '')))]);
        $data = $request->validate([
            'code' => ['required', 'string', 'max:30', 'regex:/^[A-Z0-9_]+$/', Rule::unique('roles', 'code')->ignore($id)],
            'name' => ['required', 'string', 'max:100'],
            'permissions' => ['present', 'array'],
            'permissions.*' => ['string', 'distinct'],
            'discount_max' => ['nullable', 'numeric', 'decimal:0,2', 'min:0', 'max:100'],
        ], [
            'code.unique' => 'Kode sudah dipakai (termasuk role yang sudah dihapus).',
            'code.regex' => 'Kode hanya boleh huruf besar, angka, dan garis bawah.',
        ]);

        $permissions = [];
        foreach ((array) $data['permissions'] as $permission) {
            $permission = strtolower(trim((string) $permission));
            if (! PermissionCatalog::isValid($permission) || PermissionCatalog::discountMax($permission) !== null) {
                throw ValidationException::withMessages(['permissions' => "Permission tidak dikenal: {$permission}"]);
            }
            $permissions[] = $permission;
        }
        $discount = $data['discount_max'] ?? null;
        if ($discount !== null && $discount !== '') {
            $permission = PermissionCatalog::DISCOUNT_MAX.':'.$this->trimNumber((string) $discount);
            if (PermissionCatalog::discountMax($permission) === null) {
                throw ValidationException::withMessages(['discount_max' => 'Batas diskon harus 0 sampai 100.']);
            }
            $permissions[] = $permission;
        }
        if (in_array(PermissionCatalog::ALL, $permissions, true)) {
            $permissions = [PermissionCatalog::ALL];
        }

        return [
            'code' => (string) $data['code'],
            'name' => trim((string) $data['name']),
            'permissions' => json_encode($permissions, JSON_THROW_ON_ERROR),
        ];
    }

    /** "10.50" → "10.5", "10.00" → "10". */
    private function trimNumber(string $value): string
    {
        $value = ltrim(trim($value), '+');
        if (str_contains($value, '.')) {
            $value = rtrim(rtrim($value, '0'), '.');
        }
        $value = ltrim($value, '0');

        return $value === '' || str_starts_with($value, '.') ? '0'.$value : $value;
    }

    /** @return list<string> */
    private function decode(mixed $json): array
    {
        if (! is_string($json) || $json === '') {
            return [];
        }
        $value = json_decode($json, true);

        return is_array($value) ? array_values(array_filter($value, 'is_string')) : [];
    }
}
