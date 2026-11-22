<?php

namespace App\Http\Controllers\Settings;

use App\Auth\OwnerGuard;
use App\Http\Controllers\Controller;
use App\Support\MasterData\MasterDataWriter;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Hash;
use Illuminate\Validation\Rule;
use Illuminate\Validation\ValidationException;
use Inertia\Inertia;
use Inertia\Response;

/**
 * Pengguna: users shared by HQ and every node (synced as master data). Usernames are lower-case and unique for ever.
 * Passwords are set by an admin (bcrypt $2y$, at least 8 characters) and never shown. Users are not deleted: Nonaktif.
 * Roles are assigned per node or for all nodes (user_roles.node_id NULL). Guards: an admin cannot deactivate
 * themselves, and no change may leave the system without an active OWNER (OwnerGuard).
 */
class UserController extends Controller
{
    public const MIN_PASSWORD = 8;

    public function index(Request $request): Response
    {
        $q = trim((string) $request->query('q', ''));
        $users = DB::table('users')
            ->whereNull('deleted_at')
            ->when($q !== '', fn ($query) => $query->where(fn ($w) => $w->where('username', 'like', "%{$q}%")
                ->orWhere('full_name', 'like', "%{$q}%")->orWhere('phone', 'like', "%{$q}%")))
            ->orderBy('username')
            ->select(['id', 'username', 'full_name', 'phone', 'is_active', 'version'])
            ->paginate(25)
            ->withQueryString();

        $ids = collect($users->items())->pluck('id')->all();
        $assignments = DB::table('user_roles as ur')
            ->join('roles as r', 'r.id', '=', 'ur.role_id')
            ->leftJoin('nodes as n', 'n.id', '=', 'ur.node_id')
            ->whereIn('ur.user_id', $ids)
            ->whereNull('ur.deleted_at')
            ->whereNull('r.deleted_at')
            ->orderBy('r.name')
            ->get(['ur.id', 'ur.user_id', 'ur.role_id', 'r.code as role_code', 'r.name as role_name', 'ur.node_id', 'n.code as node_code'])
            ->groupBy('user_id');

        $users->through(function (object $u) use ($assignments) {
            $row = (array) $u;

            return $row + ['roles' => $assignments->get($row['id'], collect())->values()->all()];
        });

        return Inertia::render('settings/users', [
            'q' => $q,
            'items' => $users,
            'roles' => DB::table('roles')->whereNull('deleted_at')->orderBy('name')->get(['id', 'code', 'name']),
            'nodes' => DB::table('nodes')->whereNull('deleted_at')->orderBy('code')->get(['id', 'code', 'name']),
            'minPassword' => self::MIN_PASSWORD,
        ]);
    }

    public function store(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $data = $this->validated($request);
        $password = $request->validate($this->passwordRules())['password'];
        $writer->create('users', $data + ['password_hash' => Hash::make((string) $password)]);

        return $this->saved('Pengguna disimpan.');
    }

    public function update(Request $request, string $id, MasterDataWriter $writer, OwnerGuard $owners): RedirectResponse
    {
        $this->active($id);
        $data = $this->validated($request, $id);
        if (! $data['is_active'] && $id === $request->user()?->getAuthIdentifier()) {
            throw ValidationException::withMessages(['is_active' => 'Anda tidak bisa menonaktifkan akun Anda sendiri.']);
        }
        $owners->protect(fn () => $writer->update('users', $id, $data), 'is_active');

        return $this->saved('Pengguna diperbarui.');
    }

    public function password(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $this->active($id);
        $password = $request->validate($this->passwordRules())['password'];
        $writer->update('users', $id, ['password_hash' => Hash::make((string) $password)]);

        return $this->saved('Password diganti.');
    }

    public function assignRole(Request $request, string $id, MasterDataWriter $writer): RedirectResponse
    {
        $this->active($id);
        $data = $request->validate([
            'role_id' => ['required', 'string', Rule::exists('roles', 'id')->whereNull('deleted_at')],
            'node_id' => ['nullable', 'string', Rule::exists('nodes', 'id')->whereNull('deleted_at')],
        ]);
        $node = ($data['node_id'] ?? '') === '' ? null : (string) $data['node_id'];
        $exists = DB::table('user_roles')->where('user_id', $id)->where('role_id', $data['role_id'])
            ->whereNull('deleted_at')
            ->when($node === null, fn ($q) => $q->whereNull('node_id'), fn ($q) => $q->where('node_id', $node))
            ->exists();
        if ($exists) {
            throw ValidationException::withMessages(['role_id' => 'Role ini sudah diberikan untuk node tersebut.']);
        }
        $writer->create('user_roles', ['user_id' => $id, 'role_id' => (string) $data['role_id'], 'node_id' => $node]);

        return $this->saved('Role diberikan.');
    }

    public function removeRole(string $id, string $assignment, MasterDataWriter $writer, OwnerGuard $owners): RedirectResponse
    {
        abort_unless(DB::table('user_roles')->where('id', $assignment)->where('user_id', $id)->whereNull('deleted_at')->exists(), 404);
        $owners->protect(fn () => $writer->delete('user_roles', $assignment), 'role');

        return $this->saved('Role dicabut.');
    }

    /** @return array{username: string, full_name: string, phone: ?string, is_active: bool} */
    private function validated(Request $request, ?string $id = null): array
    {
        $request->merge(['username' => mb_strtolower(trim((string) $request->input('username', '')))]);
        $data = $request->validate([
            'username' => ['required', 'string', 'min:3', 'max:50', 'regex:/^[a-z0-9._-]+$/', Rule::unique('users', 'username')->ignore($id)],
            'full_name' => ['required', 'string', 'max:200'],
            'phone' => ['nullable', 'string', 'max:20', 'regex:/^[0-9+() -]+$/'],
            'is_active' => ['boolean'],
        ], [
            'username.unique' => 'Username sudah dipakai (termasuk pengguna yang sudah dihapus).',
            'username.regex' => 'Username hanya boleh huruf kecil, angka, titik, garis bawah, dan tanda minus.',
            'phone.regex' => 'No HP hanya boleh angka, spasi, +, ( ) dan -.',
        ]);

        return [
            'username' => (string) $data['username'],
            'full_name' => trim((string) $data['full_name']),
            'phone' => trim((string) ($data['phone'] ?? '')) === '' ? null : trim((string) $data['phone']),
            'is_active' => (bool) ($data['is_active'] ?? true),
        ];
    }

    /** @return array<string, list<string>> */
    private function passwordRules(): array
    {
        return ['password' => ['required', 'string', 'min:'.self::MIN_PASSWORD, 'max:72', 'confirmed']];
    }

    private function active(string $id): void
    {
        abort_unless(DB::table('users')->where('id', $id)->whereNull('deleted_at')->exists(), 404);
    }
}
