<?php

use App\Auth\PermissionCatalog;

/*
| The official permission list: valid names, discount.max:N, and the same names as the node (Pos.Shared.Auth.Permissions
| in C#) and as the HQ routes use.
*/

it('accepts listed names, "*" and discount.max between 0 and 100', function (string $permission, bool $valid) {
    expect(PermissionCatalog::isValid($permission))->toBe($valid);
})->with([
    ['*', true],
    ['sale.create', true],
    ['users.manage', true],
    ['discount.max:0', true],
    ['discount.max:10.5', true],
    ['discount.max:100', true],
    ['discount.max:100.01', false],
    ['discount.max:-1', false],
    ['discount.max:abc', false],
    ['discount.max:', false],
    ['discount.max:5.123', false],
    ['sale.terbang', false],
    ['', false],
]);

it('gives every permission an Indonesian label and lists each name once', function () {
    $names = PermissionCatalog::names();
    expect($names)->toBe(array_values(array_unique($names)));
    foreach (PermissionCatalog::groups() as $group) {
        foreach ($group['items'] as $item) {
            expect($item['label'])->not->toBe('')->and($item['name'])->toMatch('/^[a-z]+(\.[a-z]+)+$/');
        }
    }
});

it('knows every permission the node checks (Pos.Shared.Auth.Permissions)', function () {
    $source = (string) file_get_contents(__DIR__.'/../../../node/Pos.Shared/Auth/PermissionSet.cs');
    preg_match_all('/public const string \w+ = "([^"]+)";/', $source, $m);
    $node = array_values(array_diff($m[1], [PermissionCatalog::ALL]));

    expect($node)->not->toBeEmpty()
        ->and(array_values(array_diff($node, PermissionCatalog::names())))->toBe([]);
});

it('knows every permission the HQ routes require', function () {
    $routes = (string) file_get_contents(__DIR__.'/../../routes/web.php');
    preg_match_all("/permission:([a-z.,]+)'/", $routes, $m);
    $used = array_unique(array_merge(...array_map(fn (string $list) => explode(',', $list), $m[1])));

    expect($used)->not->toBeEmpty()
        ->and(array_values(array_diff($used, PermissionCatalog::names())))->toBe([]);
});
