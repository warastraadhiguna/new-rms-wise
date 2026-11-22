<?php

namespace App\Support\Catalog;

/** One prices row as the history needs it (effective_from in UTC, 'Y-m-d H:i:s.v'). */
final readonly class PriceRow
{
    public function __construct(
        public string $id,
        public string $productUnitId,
        public ?string $branchId,
        public string $minQty,
        public string $effectiveFrom,
    ) {}

    public static function from(object $row): self
    {
        $get = fn (string $name): ?string => isset($row->{$name}) && is_scalar($row->{$name}) ? (string) $row->{$name} : null;

        return new self((string) $get('id'), (string) $get('product_unit_id'), $get('branch_id'), (string) $get('min_qty'), (string) $get('effective_from'));
    }
}
