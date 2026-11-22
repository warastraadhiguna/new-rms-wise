/** Laravel's length-aware paginator as Inertia receives it. */
export type Paginated<T> = {
    data: T[];
    current_page: number;
    last_page: number;
    per_page: number;
    total: number;
    from: number | null;
    to: number | null;
    links: { url: string | null; label: string; active: boolean }[];
};

export type Option = { id: string; name: string };

export type UnitOption = { id: string; code: string; name: string };

export type BranchOption = { id: string; code: string; name: string };

export type StatusOption = { value: string; label: string };

export type PriceStatus = 'current' | 'future' | 'history';

export type Price = {
    id: string;
    branch_id: string | null;
    branch_name: string | null;
    price: string;
    min_qty: string;
    /** Branch-local "Y-m-d H:i". */
    effective_from: string;
    created_by_name: string | null;
    status: PriceStatus;
};

export type ProductUnit = {
    id: string;
    unit_id: string;
    unit_code: string;
    unit_name: string;
    conversion: string;
    is_base: boolean;
    is_default_sale: boolean;
    barcodes: { id: string; barcode: string }[];
    prices: Price[];
};

export type Product = {
    id: string;
    sku: string;
    name: string;
    category_id: string | null;
    brand_id: string | null;
    base_unit_id: string;
    shelf_location: string | null;
    track_expiry: boolean;
    allow_qty_multiplier: boolean;
    min_stock: string | null;
    status: string;
    version: number;
    units: ProductUnit[];
};

export type ProductLookups = {
    units: UnitOption[];
    categories: Option[];
    brands: Option[];
    branches: BranchOption[];
    pricingUniform: boolean;
    statuses: StatusOption[];
};
