<?php

namespace App\Models;

use App\Auth\PermissionSet;
use App\Auth\UserPermissions;
use Illuminate\Foundation\Auth\User as Authenticatable;

/**
 * A user from the shared master data table `users` (same rows as at the nodes): id CHAR(36) UUID v7, login by
 * username, bcrypt password_hash ($2y$, verified identically by the Node Service). Inactive or soft-deleted users
 * cannot log in. Changes go through the master data writer (version + audit), never through save() here.
 *
 * @property string $id
 * @property string $username
 * @property string $password_hash
 * @property string|null $pin_hash
 * @property string $full_name
 * @property string|null $phone
 * @property bool $is_active
 * @property int $version
 * @property string|null $deleted_at
 */
class User extends Authenticatable
{
    protected $table = 'users';

    protected $keyType = 'string';

    public $incrementing = false;

    public $timestamps = false;

    /** Nothing is mass assignable: writes go through App\Support\MasterData\MasterDataWriter. */
    protected $guarded = ['*'];

    protected $hidden = ['password_hash', 'pin_hash'];

    private ?PermissionSet $permissionSet = null;

    protected function casts(): array
    {
        return [
            'is_active' => 'boolean',
            'version' => 'integer',
        ];
    }

    public function getAuthPassword(): string
    {
        return $this->password_hash;
    }

    public function getAuthPasswordName(): string
    {
        return 'password_hash';
    }

    /** The users table has no remember token column ("remember me" is not offered). */
    public function getRememberTokenName(): string
    {
        return '';
    }

    public function isActiveAccount(): bool
    {
        return $this->is_active && $this->deleted_at === null;
    }

    /** Merged permissions of the user's roles at HQ (node_id NULL or the HQ node), cached for this request. */
    public function permissions(): PermissionSet
    {
        return $this->permissionSet ??= app(UserPermissions::class)->for($this->id);
    }

    public function hasPermission(string $permission): bool
    {
        return $this->permissions()->has($permission);
    }
}
