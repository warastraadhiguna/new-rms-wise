<?php

namespace App\Auth;

use InvalidArgumentException;

/**
 * Merged permissions of a user's roles (roles.permissions, a JSON array such as ["sale.create","discount.max:10"]),
 * the same rules as Pos.Shared.Auth.PermissionSet at the node: "*" grants everything, names are lower-case.
 */
final class PermissionSet
{
    public const ALL = '*';

    /** @var array<string, true> */
    private array $names = [];

    /** @param iterable<string> $names */
    public function __construct(iterable $names = [])
    {
        foreach ($names as $name) {
            $name = strtolower(trim($name));
            if ($name !== '') {
                $this->names[$name] = true;
            }
        }
        ksort($this->names, SORT_STRING);
    }

    /**
     * @param  iterable<string|null>  $roleJson  one JSON array (or null) per role
     *
     * @throws InvalidArgumentException a role's permissions are not a JSON array of strings
     */
    public static function fromRoleJson(iterable $roleJson): self
    {
        $names = [];
        foreach ($roleJson as $json) {
            if ($json === null || trim($json) === '') {
                continue;
            }
            $decoded = json_decode($json, true, flags: JSON_THROW_ON_ERROR);
            if ($decoded === null) {
                continue;
            }
            if (! is_array($decoded) || ! array_is_list($decoded)) {
                throw new InvalidArgumentException("Role permissions must be a JSON array: {$json}");
            }
            foreach ($decoded as $name) {
                if (! is_string($name)) {
                    throw new InvalidArgumentException("Role permissions must be strings: {$json}");
                }
                $names[] = $name;
            }
        }

        return new self($names);
    }

    public function has(string $permission): bool
    {
        return isset($this->names[self::ALL]) || isset($this->names[strtolower(trim($permission))]);
    }

    public function isEmpty(): bool
    {
        return $this->names === [];
    }

    /** @return list<string> */
    public function names(): array
    {
        return array_keys($this->names);
    }
}
