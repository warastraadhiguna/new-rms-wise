import { Head, useForm } from '@inertiajs/react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import { ConfirmDelete, Field, PageHeader } from '@/components/master-data';
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

type Rep = {
    id: string;
    name: string;
    phone: string | null;
    notes: string | null;
    is_active: number | boolean;
};

type SupplierProduct = {
    id: string;
    product_id: string;
    sku: string;
    name: string;
    supplier_sku: string | null;
    sales_rep_id: string | null;
    sales_rep_name: string | null;
};

type Props = {
    supplier: {
        id: string;
        code: string;
        name: string;
        company_name: string | null;
        phone: string | null;
        bank_account: string | null;
        is_active: number | boolean;
    };
    reps: Rep[];
    products: SupplierProduct[];
};

const LIST = '/master-data/suppliers';

function RepDialog({
    base,
    rep,
    onClose,
}: {
    base: string;
    rep: Rep | null;
    onClose: () => void;
}) {
    const form = useForm({
        name: rep?.name ?? '',
        phone: rep?.phone ?? '',
        notes: rep?.notes ?? '',
        is_active: rep ? Boolean(rep.is_active) : true,
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = { preserveScroll: true, onSuccess: onClose };
        if (rep) {
            form.put(`${base}/reps/${rep.id}`, options);
        } else {
            form.post(`${base}/reps`, options);
        }
    };

    return (
        <Dialog open onOpenChange={(o) => !o && onClose()}>
            <DialogContent>
                <form onSubmit={submit} className="grid gap-4">
                    <DialogHeader>
                        <DialogTitle>
                            {rep ? 'Ubah Salesman' : 'Tambah Salesman'}
                        </DialogTitle>
                    </DialogHeader>
                    <Field
                        label="Nama"
                        htmlFor="rep-name"
                        error={form.errors.name}
                    >
                        <Input
                            id="rep-name"
                            value={form.data.name}
                            onChange={(e) =>
                                form.setData('name', e.target.value)
                            }
                            maxLength={250}
                            autoFocus
                        />
                    </Field>
                    <Field
                        label="Telepon / HP"
                        htmlFor="rep-phone"
                        error={form.errors.phone}
                    >
                        <Input
                            id="rep-phone"
                            value={form.data.phone}
                            onChange={(e) =>
                                form.setData('phone', e.target.value)
                            }
                            maxLength={50}
                        />
                    </Field>
                    <Field
                        label="Catatan"
                        htmlFor="rep-notes"
                        error={form.errors.notes}
                    >
                        <Input
                            id="rep-notes"
                            value={form.data.notes}
                            onChange={(e) =>
                                form.setData('notes', e.target.value)
                            }
                            maxLength={2000}
                        />
                    </Field>
                    <label className="flex items-center gap-2 text-sm">
                        <Checkbox
                            checked={form.data.is_active}
                            onCheckedChange={(v) =>
                                form.setData('is_active', v === true)
                            }
                        />
                        Aktif
                    </label>
                    <DialogFooter>
                        <Button
                            type="button"
                            variant="outline"
                            onClick={onClose}
                        >
                            Batal
                        </Button>
                        <Button type="submit" disabled={form.processing}>
                            Simpan
                        </Button>
                    </DialogFooter>
                </form>
            </DialogContent>
        </Dialog>
    );
}

export default function Supplier({ supplier, reps, products }: Props) {
    const base = `${LIST}/${supplier.id}`;
    const [repDialog, setRepDialog] = useState<{ rep: Rep | null } | null>(
        null,
    );
    const [deletingRep, setDeletingRep] = useState<Rep | null>(null);
    const [deletingProduct, setDeletingProduct] =
        useState<SupplierProduct | null>(null);
    const add = useForm({ product: '', supplier_sku: '', sales_rep_id: '' });

    const addProduct = (e: FormEvent) => {
        e.preventDefault();
        add.post(`${base}/products`, {
            preserveScroll: true,
            onSuccess: () => add.reset(),
        });
    };

    return (
        <>
            <Head title={`Supplier ${supplier.name}`} />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title={`${supplier.code} - ${supplier.name}`}
                    description={[
                        supplier.company_name,
                        supplier.phone,
                        supplier.bank_account,
                    ]
                        .filter(Boolean)
                        .join(' · ')}
                />
                <Card>
                    <CardHeader className="flex flex-row items-center justify-between">
                        <CardTitle>Salesman</CardTitle>
                        <Button
                            size="sm"
                            onClick={() => setRepDialog({ rep: null })}
                            data-test="add-rep"
                        >
                            <Plus className="size-4" /> Tambah Salesman
                        </Button>
                    </CardHeader>
                    <CardContent>
                        <Table>
                            <TableHeader>
                                <TableRow>
                                    <TableHead>Nama</TableHead>
                                    <TableHead>Telepon</TableHead>
                                    <TableHead>Catatan</TableHead>
                                    <TableHead className="w-24">
                                        Status
                                    </TableHead>
                                    <TableHead className="w-24 text-right">
                                        Aksi
                                    </TableHead>
                                </TableRow>
                            </TableHeader>
                            <TableBody>
                                {reps.map((r) => (
                                    <TableRow key={r.id}>
                                        <TableCell>{r.name}</TableCell>
                                        <TableCell>{r.phone}</TableCell>
                                        <TableCell>{r.notes}</TableCell>
                                        <TableCell>
                                            {r.is_active ? (
                                                <Badge variant="secondary">
                                                    Aktif
                                                </Badge>
                                            ) : (
                                                <Badge variant="outline">
                                                    Nonaktif
                                                </Badge>
                                            )}
                                        </TableCell>
                                        <TableCell className="text-right whitespace-nowrap">
                                            <Button
                                                variant="ghost"
                                                size="icon"
                                                title="Ubah"
                                                onClick={() =>
                                                    setRepDialog({ rep: r })
                                                }
                                            >
                                                <Pencil className="size-4" />
                                            </Button>
                                            <Button
                                                variant="ghost"
                                                size="icon"
                                                title="Hapus"
                                                onClick={() =>
                                                    setDeletingRep(r)
                                                }
                                            >
                                                <Trash2 className="size-4" />
                                            </Button>
                                        </TableCell>
                                    </TableRow>
                                ))}
                                {reps.length === 0 && (
                                    <TableRow>
                                        <TableCell
                                            colSpan={5}
                                            className="py-4 text-center text-muted-foreground"
                                        >
                                            Belum ada salesman.
                                        </TableCell>
                                    </TableRow>
                                )}
                            </TableBody>
                        </Table>
                    </CardContent>
                </Card>
                <Card>
                    <CardHeader>
                        <CardTitle>Barang yang dipasok</CardTitle>
                    </CardHeader>
                    <CardContent className="grid gap-3">
                        <form
                            onSubmit={addProduct}
                            className="flex flex-wrap items-start gap-2"
                        >
                            <Field
                                label="SKU atau barcode"
                                htmlFor="sp-product"
                                error={add.errors.product}
                            >
                                <Input
                                    id="sp-product"
                                    className="w-56"
                                    value={add.data.product}
                                    onChange={(e) =>
                                        add.setData('product', e.target.value)
                                    }
                                    data-test="supplier-product-input"
                                />
                            </Field>
                            <Field
                                label="Kode di supplier"
                                htmlFor="sp-sku"
                                error={add.errors.supplier_sku}
                            >
                                <Input
                                    id="sp-sku"
                                    className="w-44"
                                    value={add.data.supplier_sku}
                                    onChange={(e) =>
                                        add.setData(
                                            'supplier_sku',
                                            e.target.value,
                                        )
                                    }
                                    maxLength={50}
                                />
                            </Field>
                            <Field
                                label="Salesman"
                                htmlFor="sp-rep"
                                error={add.errors.sales_rep_id}
                            >
                                <NativeSelect
                                    id="sp-rep"
                                    className="w-48"
                                    value={add.data.sales_rep_id}
                                    onChange={(e) =>
                                        add.setData(
                                            'sales_rep_id',
                                            e.target.value,
                                        )
                                    }
                                >
                                    <option value="">—</option>
                                    {reps
                                        .filter((r) => r.is_active)
                                        .map((r) => (
                                            <option key={r.id} value={r.id}>
                                                {r.name}
                                            </option>
                                        ))}
                                </NativeSelect>
                            </Field>
                            <Button
                                type="submit"
                                className="mt-6"
                                disabled={add.processing}
                            >
                                <Plus className="size-4" /> Tambah
                            </Button>
                        </form>
                        <Table>
                            <TableHeader>
                                <TableRow>
                                    <TableHead className="w-36">SKU</TableHead>
                                    <TableHead>Nama barang</TableHead>
                                    <TableHead>Kode di supplier</TableHead>
                                    <TableHead>Salesman</TableHead>
                                    <TableHead className="w-16 text-right">
                                        Aksi
                                    </TableHead>
                                </TableRow>
                            </TableHeader>
                            <TableBody>
                                {products.map((p) => (
                                    <TableRow key={p.id}>
                                        <TableCell className="font-mono">
                                            {p.sku}
                                        </TableCell>
                                        <TableCell>{p.name}</TableCell>
                                        <TableCell>{p.supplier_sku}</TableCell>
                                        <TableCell>
                                            {p.sales_rep_name}
                                        </TableCell>
                                        <TableCell className="text-right">
                                            <Button
                                                variant="ghost"
                                                size="icon"
                                                title="Hapus"
                                                onClick={() =>
                                                    setDeletingProduct(p)
                                                }
                                            >
                                                <Trash2 className="size-4" />
                                            </Button>
                                        </TableCell>
                                    </TableRow>
                                ))}
                                {products.length === 0 && (
                                    <TableRow>
                                        <TableCell
                                            colSpan={5}
                                            className="py-4 text-center text-muted-foreground"
                                        >
                                            Belum ada barang.
                                        </TableCell>
                                    </TableRow>
                                )}
                            </TableBody>
                        </Table>
                    </CardContent>
                </Card>
            </div>
            {repDialog && (
                <RepDialog
                    key={repDialog.rep?.id ?? 'new'}
                    base={base}
                    rep={repDialog.rep}
                    onClose={() => setRepDialog(null)}
                />
            )}
            {deletingRep && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeletingRep(null)}
                    title="Hapus salesman?"
                    message={`Salesman ${deletingRep.name} akan dihapus dan dilepas dari barang yang dipasoknya.`}
                    url={`${base}/reps/${deletingRep.id}`}
                />
            )}
            {deletingProduct && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeletingProduct(null)}
                    title="Hapus barang dari supplier?"
                    message={`${deletingProduct.sku} - ${deletingProduct.name} dihapus dari daftar barang supplier ini.`}
                    url={`${base}/products/${deletingProduct.id}`}
                />
            )}
        </>
    );
}

Supplier.layout = {
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: 'Supplier', href: LIST },
    ],
};
