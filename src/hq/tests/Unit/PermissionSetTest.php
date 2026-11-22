<?php

use App\Auth\PermissionSet;

it('merges role JSON like the node: lower-case names, "*" grants all', function () {
    $set = PermissionSet::fromRoleJson(['["Sale.Create","hq.access"]', null, 'null', '["hq.access","discount.max:10"]']);

    expect($set->names())->toBe(['discount.max:10', 'hq.access', 'sale.create'])
        ->and($set->has('SALE.CREATE'))->toBeTrue()
        ->and($set->has('stock.view'))->toBeFalse()
        ->and(PermissionSet::fromRoleJson(['["*"]'])->has('stock.view'))->toBeTrue()
        ->and(PermissionSet::fromRoleJson([])->isEmpty())->toBeTrue();
});

it('rejects permissions that are not a JSON array of strings', function () {
    expect(fn () => PermissionSet::fromRoleJson(['{"a":1}']))->toThrow(InvalidArgumentException::class)
        ->and(fn () => PermissionSet::fromRoleJson(['[1]']))->toThrow(InvalidArgumentException::class);
});
