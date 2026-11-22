import { Head, router, useForm, usePage } from '@inertiajs/react';
import { Pencil, Plus, Save, Trash2, X } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import {
    ConfirmDelete,
    Field,
    formatNumber,
    PageHeader,
} from '@/components/master-data';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Checkbox } from '@/components/ui/checkbox';
import {
    Dialog,
    DialogContent,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from '@/components/ui/table';
import type { Product, ProductLookups, ProductUnit } from '@/types/master-data';

type Props = ProductLookups & { product: Product | null };

const LIST = '/master-data/products';

const priceStatus: Record<
    string,
    { label: string; variant: 'default' | 'secondary' | 'outline' }
> = {
    current: { label: 'Berlaku', variant: 'default' },
    future: { label: 'Akan berlaku', variant: 'secondary' },
    history: { label: 'Riwayat', variant: 'outline' },
};

function today(): string {
    return new Date().toLocaleDateString('en-CA', { timeZone: 'Asia/Jakarta' });
}

/** Data Barang: the product fields (create also takes the base unit's barcodes and first price). */
function ProductFields({
    product,
    units,
    categories,
    brands,
    statuses,
}: Props) {
    const form = useForm({
        sku: product?.sku ?? '',
        name: product?.name ?? '',
        category_id: product?.category_id ?? '',
        brand_id: product?.brand_id ?? '',
        base_unit_id: product?.base_unit_id ?? '',
        shelf_location: product?.shelf_location ?? '',
        track_expiry: product?.track_expiry ?? false,
        allow_qty_multiplier: product?.allow_qty_multiplier ?? true,
        min_stock: product?.min_stock ?? '',
        status: product?.status ?? 'ACTIVE',
        barcodes: '',
        price: '',
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        form.transform((data) => ({
            ...data,
            category_id: data.category_id || null,
            brand_id: data.brand_id || null,
            min_stock: data.min_stock === '' ? null : data.min_stock,
            barcodes: data.barcodes
                .split(/[\n,]/)
                .map((b) => b.trim())
                .filter((b) => b !== ''),
            price: data.price === '' ? null : data.price,
        }));
        if (product) {
            form.put(`${LIST}/${product.id}`, { preserveScroll: true });
        } else {
            form.post(LIST);
        }
    };

    const e = form.errors as Record<string, string>;

    return (
        <Card>
            <CardHeader>
                <CardTitle>Data Barang</CardTitle>
            </CardHeader>
            <CardContent>
                <form onSubmit={submit} className="grid gap-4">
                    <div className="grid gap-3 md:grid-cols-4">
                        <Field label="SKU" htmlFor="sku" error={e.sku}>
                            <Input
                                id="sku"
                                value={form.data.sku}
                                onChange={(ev) =>
                                    form.setData('sku', ev.target.value)
                                }
                                maxLength={40}
                                autoFocus={!product}
                            />
                        </Field>
                        <Field
                            label="Nama barang"
                            htmlFor="name"
                            error={e.name}
                            className="md:col-span-3"
                        >
                            <Input
                                id="name"
                                value={form.data.name}
                                onChange={(ev) =>
                                    form.setData('name', ev.target.value)
                                }
                                maxLength={200}
                            />
                        </Field>
                        <Field
                            label="Kategori"
                            htmlFor="category_id"
                            error={e.category_id}
                        >
                            <NativeSelect
                                id="category_id"
                                value={form.data.category_id}
                                onChange={(ev) =>
                                    form.setData('category_id', ev.target.value)
                                }
                            >
                                <option value="">— Tanpa kategori —</option>
                                {categories.map((c) => (
                                    <option key={c.id} value={c.id}>
                                        {c.name}
                                    </option>
                                ))}
                            </NativeSelect>
                        </Field>
                        <Field
                            label="Merek"
                            htmlFor="brand_id"
                            error={e.brand_id}
                        >
                            <NativeSelect
                                id="brand_id"
                                value={form.data.brand_id}
                                onChange={(ev) =>
                                    form.setData('brand_id', ev.target.value)
                                }
                            >
                                <option value="">— Tanpa merek —</option>
                                {brands.map((b) => (
                                    <option key={b.id} value={b.id}>
                                        {b.name}
                                    </option>
                                ))}
                            </NativeSelect>
                        </Field>
                        <Field
                            label="Satuan dasar"
                            htmlFor="base_unit_id"
                            error={e.base_unit_id}
                        >
                            <NativeSelect
                                id="base_unit_id"
                                value={form.data.base_unit_id}
                                onChange={(ev) =>
                                    form.setData(
                                        'base_unit_id',
                                        ev.target.value,
                                    )
                                }
                                disabled={product !== null}
                                title={
                                    product
                                        ? 'Satuan dasar tidak bisa diubah setelah barang dibuat'
                                        : undefined
                                }
                            >
                                <option value="">— Pilih —</option>
                                {units.map((u) => (
                                    <option key={u.id} value={u.id}>
                                        {u.code} - {u.name}
                                    </option>
                                ))}
                            </NativeSelect>
                        </Field>
                        <Field
                            label="Letak"
                            htmlFor="shelf_location"
                            error={e.shelf_location}
                        >
                            <Input
                                id="shelf_location"
                                value={form.data.shelf_location}
                                onChange={(ev) =>
                                    form.setData(
                                        'shelf_location',
                                        ev.target.value,
                                    )
                                }
                                maxLength={40}
                                placeholder="Rak A-3"
                            />
                        </Field>
                        <Field
                            label="Stok minimum (satuan dasar)"
                            htmlFor="min_stock"
                            error={e.min_stock}
                        >
                            <Input
                                id="min_stock"
                                inputMode="decimal"
                                value={form.data.min_stock}
                                onChange={(ev) =>
                                    form.setData('min_stock', ev.target.value)
                                }
                            />
                        </Field>
                        <Field label="Status" htmlFor="status" error={e.status}>
                            <NativeSelect
                                id="status"
                                value={form.data.status}
                                onChange={(ev) =>
                                    form.setData('status', ev.target.value)
                                }
                            >
                                {statuses.map((s) => (
                                    <option key={s.value} value={s.value}>
                                        {s.label}
                                    </option>
                                ))}
                            </NativeSelect>
                        </Field>
                        <div className="flex flex-col justify-end gap-2 md:col-span-2">
                            <label className="flex items-center gap-2 text-sm">
                                <Checkbox
                                    checked={form.data.track_expiry}
                                    onCheckedChange={(v) =>
                                        form.setData('track_expiry', v === true)
                                    }
                                />
                                Lacak tanggal expired (FEFO)
                            </label>
                            <label className="flex items-center gap-2 text-sm">
                                <Checkbox
                                    checked={form.data.allow_qty_multiplier}
                                    onCheckedChange={(v) =>
                                        form.setData(
                                            'allow_qty_multiplier',
                                            v === true,
                                        )
                                    }
                                />
                                Boleh pengali qty di kasir (tanpa centang:
                                setiap barang wajib discan)
                            </label>
                        </div>
                    </div>
                    {!product && (
                        <div className="grid gap-3 md:grid-cols-4">
                            <Field
                                label="Barcode satuan dasar (satu per baris)"
                                htmlFor="barcodes"
                                error={
                                    e.barcodes ??
                                    Object.entries(e).find(([k]) =>
                                        k.startsWith('barcodes.'),
                                    )?.[1]
                                }
                                className="md:col-span-2"
                            >
                                <textarea
                                    id="barcodes"
                                    className="min-h-16 rounded-md border border-input bg-transparent px-2 py-1 text-sm"
                                    value={form.data.barcodes}
                                    onChange={(ev) =>
                                        form.setData(
                                            'barcodes',
                                            ev.target.value,
                                        )
                                    }
                                />
                            </Field>
                            <Field
                                label="Harga jual satuan dasar (berlaku hari ini)"
                                htmlFor="price"
                                error={e.price ?? e.barcode}
                            >
                                <Input
                                    id="price"
                                    inputMode="decimal"
                                    value={form.data.price}
                                    onChange={(ev) =>
                                        form.setData('price', ev.target.value)
                                    }
                                    placeholder="Boleh dikosongkan"
                                />
                            </Field>
                        </div>
                    )}
                    <div className="flex justify-end">
                        <Button
                            type="submit"
                            disabled={form.processing}
                            data-test="save-product"
                        >
                            <Save className="size-4" /> Simpan
                        </Button>
                    </div>
                </form>
            </CardContent>
        </Card>
    );
}

/** Add a selling unit or change one (conversion, default sale unit). */
function UnitDialog({
    product,
    unit,
    units,
    open,
    onOpenChange,
}: {
    product: Product;
    unit: ProductUnit | null;
    units: ProductLookups['units'];
    open: boolean;
    onOpenChange: (o: boolean) => void;
}) {
    const form = useForm({
        unit_id: unit?.unit_id ?? '',
        conversion: unit?.conversion ?? '',
        is_default_sale: unit?.is_default_sale ?? false,
        barcodes: '',
    });
    const base = units.find((u) => u.id === product.base_unit_id);
    const taken = new Set(product.units.map((u) => u.unit_id));

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = {
            preserveScroll: true,
            onSuccess: () => onOpenChange(false),
        };
        if (unit) {
            form.put(`${LIST}/${product.id}/units/${unit.id}`, options);
        } else {
            form.transform((d) => ({
                ...d,
                barcodes: d.barcodes
                    .split(/[\n,]/)
                    .map((b) => b.trim())
                    .filter((b) => b !== ''),
            }));
            form.post(`${LIST}/${product.id}/units`, options);
        }
    };

    const e = form.errors as Record<string, string>;

    return (
        <Dialog open={open} onOpenChange={onOpenChange}>
            <DialogContent>
                <form onSubmit={submit} className="grid gap-4">
                    <DialogHeader>
                        <DialogTitle>
                            {unit
                                ? `Ubah Satuan ${unit.unit_code}`
                                : 'Tambah Satuan Jual'}
                        </DialogTitle>
                    </DialogHeader>
                    {!unit && (
                        <Field
                            label="Satuan"
                            htmlFor="unit_id"
                            error={e.unit_id}
                        >
                            <NativeSelect
                                id="unit_id"
                                value={form.data.unit_id}
                                onChange={(ev) =>
                                    form.setData('unit_id', ev.target.value)
                                }
                            >
                                <option value="">— Pilih —</option>
                                {units
                                    .filter((u) => !taken.has(u.id))
                                    .map((u) => (
                                        <option key={u.id} value={u.id}>
                                            {u.code} - {u.name}
                                        </option>
                                    ))}
                            </NativeSelect>
                        </Field>
                    )}
                    <Field
                        label={`Konversi (isi dalam ${base?.code ?? 'satuan dasar'})`}
                        htmlFor="conversion"
                        error={e.conversion}
                    >
                        <Input
                            id="conversion"
                            inputMode="decimal"
                            value={form.data.conversion}
                            onChange={(ev) =>
                                form.setData('conversion', ev.target.value)
                            }
                            disabled={unit?.is_base}
                            placeholder="1 BOX = 24 PCS → 24"
                        />
                    </Field>
                    {!unit && (
                        <Field
                            label="Barcode (satu per baris)"
                            htmlFor="unit_barcodes"
                            error={
                                e.barcodes ??
                                e.barcode ??
                                Object.entries(e).find(([k]) =>
                                    k.startsWith('barcodes.'),
                                )?.[1]
                            }
                        >
                            <textarea
                                id="unit_barcodes"
                                className="min-h-16 rounded-md border border-input bg-transparent px-2 py-1 text-sm"
                                value={form.data.barcodes}
                                onChange={(ev) =>
                                    form.setData('barcodes', ev.target.value)
                                }
                            />
                        </Field>
                    )}
                    <label className="flex items-center gap-2 text-sm">
                        <Checkbox
                            checked={form.data.is_default_sale}
                            onCheckedChange={(v) =>
                                form.setData('is_default_sale', v === true)
                            }
                        />
                        Satuan jual utama
                    </label>
                    <DialogFooter>
                        <Button
                            type="button"
                            variant="outline"
                            onClick={() => onOpenChange(false)}
                        >
                            Batal
                        </Button>
                        <Button
                            type="submit"
                            disabled={form.processing}
                            data-test="save-unit"
                        >
                            Simpan
                        </Button>
                    </DialogFooter>
                </form>
            </DialogContent>
        </Dialog>
    );
}

function BarcodeAdder({
    product,
    unit,
}: {
    product: Product;
    unit: ProductUnit;
}) {
    const form = useForm({ barcode: '' });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        form.post(`${LIST}/${product.id}/units/${unit.id}/barcodes`, {
            preserveScroll: true,
            onSuccess: () => form.reset(),
        });
    };

    return (
        <form onSubmit={submit} className="flex flex-col gap-1">
            <div className="flex gap-1">
                <Input
                    className="h-7 w-40 text-xs"
                    value={form.data.barcode}
                    onChange={(e) => form.setData('barcode', e.target.value)}
                    placeholder="Tambah barcode"
                />
                <Button
                    type="submit"
                    size="sm"
                    variant="outline"
                    className="h-7"
                    disabled={form.processing}
                    title="Tambah barcode"
                >
                    <Plus className="size-3" />
                </Button>
            </div>
            {form.errors.barcode && (
                <span className="text-xs text-destructive">
                    {form.errors.barcode}
                </span>
            )}
        </form>
    );
}

function UnitsCard({
    product,
    units,
}: {
    product: Product;
    units: ProductLookups['units'];
}) {
    const [editing, setEditing] = useState<ProductUnit | null>(null);
    const [formOpen, setFormOpen] = useState(false);
    const [deleting, setDeleting] = useState<ProductUnit | null>(null);
    const base = units.find((u) => u.id === product.base_unit_id);

    return (
        <Card>
            <CardHeader className="flex flex-row items-center justify-between">
                <CardTitle>Satuan Jual &amp; Barcode</CardTitle>
                <Button
                    size="sm"
                    onClick={() => {
                        setEditing(null);
                        setFormOpen(true);
                    }}
                    data-test="add-unit"
                >
                    <Plus className="size-4" /> Tambah Satuan
                </Button>
            </CardHeader>
            <CardContent>
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-28">Satuan</TableHead>
                            <TableHead className="w-40">Konversi</TableHead>
                            <TableHead>Barcode</TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {product.units.map((u) => (
                            <TableRow key={u.id}>
                                <TableCell>
                                    <span className="font-medium">
                                        {u.unit_code}
                                    </span>
                                    <div className="flex gap-1">
                                        {u.is_base && (
                                            <Badge variant="outline">
                                                Dasar
                                            </Badge>
                                        )}
                                        {u.is_default_sale && (
                                            <Badge variant="secondary">
                                                Jual utama
                                            </Badge>
                                        )}
                                    </div>
                                </TableCell>
                                <TableCell>
                                    1 {u.unit_code} ={' '}
                                    {formatNumber(u.conversion)} {base?.code}
                                </TableCell>
                                <TableCell>
                                    <div className="flex flex-wrap items-center gap-1">
                                        {u.barcodes.map((b) => (
                                            <Badge
                                                key={b.id}
                                                variant="outline"
                                                className="gap-1 font-mono"
                                            >
                                                {b.barcode}
                                                <button
                                                    type="button"
                                                    title="Hapus barcode"
                                                    onClick={() =>
                                                        router.delete(
                                                            `${LIST}/${product.id}/barcodes/${b.id}`,
                                                            {
                                                                preserveScroll: true,
                                                            },
                                                        )
                                                    }
                                                >
                                                    <X className="size-3" />
                                                </button>
                                            </Badge>
                                        ))}
                                        <BarcodeAdder
                                            product={product}
                                            unit={u}
                                        />
                                    </div>
                                </TableCell>
                                <TableCell className="text-right">
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Ubah"
                                        onClick={() => {
                                            setEditing(u);
                                            setFormOpen(true);
                                        }}
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                    {!u.is_base && (
                                        <Button
                                            variant="ghost"
                                            size="icon"
                                            title="Hapus"
                                            onClick={() => setDeleting(u)}
                                        >
                                            <Trash2 className="size-4" />
                                        </Button>
                                    )}
                                </TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            </CardContent>
            {formOpen && (
                <UnitDialog
                    key={editing?.id ?? 'new'}
                    product={product}
                    unit={editing}
                    units={units}
                    open={formOpen}
                    onOpenChange={setFormOpen}
                />
            )}
            {deleting && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeleting(null)}
                    title="Hapus satuan jual?"
                    message={`Satuan ${deleting.unit_code} dan barcodenya dihapus dari barang ini.`}
                    url={`${LIST}/${product.id}/units/${deleting.id}`}
                />
            )}
        </Card>
    );
}

/** New price row (never overwrites): unit, branch (when prices may differ per branch), min qty, price, from date. */
function PriceAdder({
    product,
    branches,
    pricingUniform,
}: {
    product: Product;
    branches: ProductLookups['branches'];
    pricingUniform: boolean;
}) {
    const [unitId, setUnitId] = useState(product.units[0]?.id ?? '');
    const form = useForm({
        price: '',
        min_qty: '1',
        effective_from: today(),
        branch_id: '',
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        form.transform((d) => ({ ...d, branch_id: d.branch_id || null }));
        form.post(`${LIST}/${product.id}/units/${unitId}/prices`, {
            preserveScroll: true,
            onSuccess: () => form.reset('price'),
        });
    };

    const e = form.errors as Record<string, string>;

    return (
        <form
            onSubmit={submit}
            className="grid gap-3 rounded-md border p-3 md:grid-cols-6"
        >
            <Field label="Satuan" htmlFor="price_unit">
                <NativeSelect
                    id="price_unit"
                    value={unitId}
                    onChange={(ev) => setUnitId(ev.target.value)}
                >
                    {product.units.map((u) => (
                        <option key={u.id} value={u.id}>
                            {u.unit_code}
                        </option>
                    ))}
                </NativeSelect>
            </Field>
            {!pricingUniform && (
                <Field
                    label="Cabang"
                    htmlFor="price_branch"
                    error={e.branch_id}
                >
                    <NativeSelect
                        id="price_branch"
                        value={form.data.branch_id}
                        onChange={(ev) =>
                            form.setData('branch_id', ev.target.value)
                        }
                    >
                        <option value="">Semua cabang</option>
                        {branches.map((b) => (
                            <option key={b.id} value={b.id}>
                                {b.name}
                            </option>
                        ))}
                    </NativeSelect>
                </Field>
            )}
            <Field
                label="Qty minimum (grosir)"
                htmlFor="min_qty"
                error={e.min_qty}
            >
                <Input
                    id="min_qty"
                    inputMode="decimal"
                    value={form.data.min_qty}
                    onChange={(ev) => form.setData('min_qty', ev.target.value)}
                />
            </Field>
            <Field label="Harga" htmlFor="new_price" error={e.price}>
                <Input
                    id="new_price"
                    inputMode="decimal"
                    value={form.data.price}
                    onChange={(ev) => form.setData('price', ev.target.value)}
                />
            </Field>
            <Field
                label="Berlaku mulai"
                htmlFor="effective_from"
                error={e.effective_from}
            >
                <Input
                    id="effective_from"
                    type="date"
                    min={today()}
                    value={form.data.effective_from}
                    onChange={(ev) =>
                        form.setData('effective_from', ev.target.value)
                    }
                />
            </Field>
            <div className="flex items-end">
                <Button
                    type="submit"
                    disabled={form.processing || unitId === ''}
                    data-test="save-price"
                >
                    <Plus className="size-4" /> Harga Baru
                </Button>
            </div>
        </form>
    );
}

function PricesCard({
    product,
    branches,
    pricingUniform,
}: {
    product: Product;
    branches: ProductLookups['branches'];
    pricingUniform: boolean;
}) {
    const [withdrawing, setWithdrawing] = useState<string | null>(null);
    const { errors } = usePage().props as { errors?: Record<string, string> };

    return (
        <Card>
            <CardHeader>
                <CardTitle>Harga</CardTitle>
                <p className="text-sm text-muted-foreground">
                    Harga lama tidak diubah: setiap perubahan menjadi harga baru
                    dengan tanggal berlaku.
                    {pricingUniform
                        ? ' Harga seragam untuk semua cabang (pricing.uniform aktif).'
                        : ' Harga khusus cabang menggantikan harga semua cabang di cabang itu.'}
                </p>
            </CardHeader>
            <CardContent className="grid gap-4">
                <PriceAdder
                    product={product}
                    branches={branches}
                    pricingUniform={pricingUniform}
                />
                {errors?.delete && (
                    <p className="text-sm text-destructive">{errors.delete}</p>
                )}
                {product.units.map((u) => {
                    const rows = u.prices.filter(
                        (p) => !pricingUniform || p.branch_id === null,
                    );
                    return (
                        <div key={u.id} className="grid gap-1">
                            <h3 className="text-sm font-medium">
                                Per {u.unit_code}
                            </h3>
                            <Table>
                                <TableHeader>
                                    <TableRow>
                                        {!pricingUniform && (
                                            <TableHead>Cabang</TableHead>
                                        )}
                                        <TableHead className="w-28 text-right">
                                            Qty min
                                        </TableHead>
                                        <TableHead className="w-32 text-right">
                                            Harga
                                        </TableHead>
                                        <TableHead className="w-40">
                                            Berlaku mulai
                                        </TableHead>
                                        <TableHead className="w-28">
                                            Status
                                        </TableHead>
                                        <TableHead>Oleh</TableHead>
                                        <TableHead className="w-16" />
                                    </TableRow>
                                </TableHeader>
                                <TableBody>
                                    {rows.map((p) => (
                                        <TableRow
                                            key={p.id}
                                            className={
                                                p.status === 'history'
                                                    ? 'text-muted-foreground'
                                                    : ''
                                            }
                                            data-test="price-row"
                                        >
                                            {!pricingUniform && (
                                                <TableCell>
                                                    {p.branch_name ??
                                                        'Semua cabang'}
                                                </TableCell>
                                            )}
                                            <TableCell className="text-right">
                                                {formatNumber(p.min_qty)}
                                            </TableCell>
                                            <TableCell className="text-right font-medium">
                                                {formatNumber(p.price)}
                                            </TableCell>
                                            <TableCell>
                                                {p.effective_from}
                                            </TableCell>
                                            <TableCell>
                                                <Badge
                                                    variant={
                                                        priceStatus[p.status]
                                                            .variant
                                                    }
                                                >
                                                    {
                                                        priceStatus[p.status]
                                                            .label
                                                    }
                                                </Badge>
                                            </TableCell>
                                            <TableCell>
                                                {p.created_by_name ?? '—'}
                                            </TableCell>
                                            <TableCell className="text-right">
                                                {p.status === 'future' && (
                                                    <Button
                                                        variant="ghost"
                                                        size="icon"
                                                        title="Batalkan harga yang belum berlaku"
                                                        onClick={() =>
                                                            setWithdrawing(p.id)
                                                        }
                                                    >
                                                        <Trash2 className="size-4" />
                                                    </Button>
                                                )}
                                            </TableCell>
                                        </TableRow>
                                    ))}
                                    {rows.length === 0 && (
                                        <TableRow>
                                            <TableCell
                                                colSpan={7}
                                                className="py-3 text-center text-muted-foreground"
                                            >
                                                Belum ada harga.
                                            </TableCell>
                                        </TableRow>
                                    )}
                                </TableBody>
                            </Table>
                        </div>
                    );
                })}
            </CardContent>
            {withdrawing && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setWithdrawing(null)}
                    title="Batalkan harga?"
                    message="Harga yang belum berlaku ini dibatalkan."
                    url={`${LIST}/${product.id}/prices/${withdrawing}`}
                />
            )}
        </Card>
    );
}

export default function ProductForm(props: Props) {
    const { product } = props;
    const [deleting, setDeleting] = useState(false);

    return (
        <>
            <Head title={product ? `Produk ${product.sku}` : 'Tambah Produk'} />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title={
                        product
                            ? `${product.sku} — ${product.name}`
                            : 'Tambah Produk'
                    }
                    description={
                        product
                            ? `Versi ${product.version}`
                            : 'Satuan dasar otomatis menjadi satuan jual dengan konversi 1.'
                    }
                    actions={
                        product && (
                            <Button
                                variant="outline"
                                onClick={() => setDeleting(true)}
                                data-test="delete-product"
                            >
                                <Trash2 className="size-4" /> Hapus Barang
                            </Button>
                        )
                    }
                />
                <ProductFields key={product?.version ?? 'new'} {...props} />
                {product && <UnitsCard product={product} units={props.units} />}
                {product && (
                    <PricesCard
                        product={product}
                        branches={props.branches}
                        pricingUniform={props.pricingUniform}
                    />
                )}
            </div>
            {product && deleting && (
                <ConfirmDelete
                    open
                    onOpenChange={setDeleting}
                    title="Hapus barang?"
                    message={`${product.sku} — ${product.name} akan dihapus. Hanya barang yang belum pernah dipakai yang bisa dihapus.`}
                    url={`${LIST}/${product.id}`}
                    deactivateUrl={`${LIST}/${product.id}/deactivate`}
                />
            )}
        </>
    );
}

ProductForm.layout = {
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: 'Produk', href: LIST },
    ],
};
