import { Head, Link, router } from '@inertiajs/react';
import { Plus, Search } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import { formatNumber, PageHeader, Pagination } from '@/components/master-data';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
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
import type { Paginated, ProductLookups } from '@/types/master-data';

type Row = {
    id: string;
    sku: string;
    name: string;
    status: string;
    base_unit: string;
    category: string | null;
    brand: string | null;
    price: string | null;
};

type Filters = {
    q: string;
    category_id: string;
    brand_id: string;
    status: string;
};

type Props = ProductLookups & { filters: Filters; items: Paginated<Row> };

const URL = '/master-data/products';

export const statusLabel: Record<string, string> = {
    ACTIVE: 'Aktif',
    INACTIVE: 'Nonaktif',
    TEMPORARY: 'Sementara',
};

export default function Products({
    filters,
    items,
    categories,
    brands,
    statuses,
}: Props) {
    const [form, setForm] = useState<Filters>(filters);

    const apply = (next: Filters) => {
        setForm(next);
        router.get(URL, next, {
            preserveState: true,
            preserveScroll: true,
            replace: true,
        });
    };

    const submit = (e: FormEvent) => {
        e.preventDefault();
        apply(form);
    };

    return (
        <>
            <Head title="Produk" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Produk"
                    actions={
                        <Button asChild data-test="add-button">
                            <Link href={`${URL}/create`}>
                                <Plus className="size-4" /> Tambah Produk
                            </Link>
                        </Button>
                    }
                />
                <form
                    onSubmit={submit}
                    className="flex flex-wrap items-end gap-2"
                >
                    <Input
                        className="w-72"
                        value={form.q}
                        onChange={(e) =>
                            setForm({ ...form, q: e.target.value })
                        }
                        placeholder="Cari kode, nama, atau barcode"
                        data-test="search-input"
                    />
                    <NativeSelect
                        className="w-52"
                        value={form.category_id}
                        onChange={(e) =>
                            apply({ ...form, category_id: e.target.value })
                        }
                        aria-label="Kategori"
                    >
                        <option value="">Semua kategori</option>
                        {categories.map((c) => (
                            <option key={c.id} value={c.id}>
                                {c.name}
                            </option>
                        ))}
                    </NativeSelect>
                    <NativeSelect
                        className="w-44"
                        value={form.brand_id}
                        onChange={(e) =>
                            apply({ ...form, brand_id: e.target.value })
                        }
                        aria-label="Merek"
                    >
                        <option value="">Semua merek</option>
                        {brands.map((b) => (
                            <option key={b.id} value={b.id}>
                                {b.name}
                            </option>
                        ))}
                    </NativeSelect>
                    <NativeSelect
                        className="w-36"
                        value={form.status}
                        onChange={(e) =>
                            apply({ ...form, status: e.target.value })
                        }
                        aria-label="Status"
                    >
                        <option value="">Semua status</option>
                        {statuses.map((s) => (
                            <option key={s.value} value={s.value}>
                                {s.label}
                            </option>
                        ))}
                    </NativeSelect>
                    <Button type="submit" variant="secondary">
                        <Search className="size-4" /> Cari
                    </Button>
                </form>
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-36">Kode</TableHead>
                            <TableHead>Nama Barang</TableHead>
                            <TableHead>Kategori</TableHead>
                            <TableHead>Merek</TableHead>
                            <TableHead className="w-20">Satuan</TableHead>
                            <TableHead className="w-28 text-right">
                                Harga
                            </TableHead>
                            <TableHead className="w-24">Status</TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.data.map((p) => (
                            <TableRow
                                key={p.id}
                                className="cursor-pointer"
                                onClick={() =>
                                    router.visit(`${URL}/${p.id}/edit`)
                                }
                            >
                                <TableCell className="font-mono">
                                    {p.sku}
                                </TableCell>
                                <TableCell>
                                    <Link
                                        href={`${URL}/${p.id}/edit`}
                                        className="hover:underline"
                                    >
                                        {p.name}
                                    </Link>
                                </TableCell>
                                <TableCell className="text-muted-foreground">
                                    {p.category ?? '—'}
                                </TableCell>
                                <TableCell className="text-muted-foreground">
                                    {p.brand ?? '—'}
                                </TableCell>
                                <TableCell>{p.base_unit}</TableCell>
                                <TableCell className="text-right">
                                    {p.price === null
                                        ? '—'
                                        : formatNumber(p.price)}
                                </TableCell>
                                <TableCell>
                                    <Badge
                                        variant={
                                            p.status === 'ACTIVE'
                                                ? 'secondary'
                                                : 'outline'
                                        }
                                    >
                                        {statusLabel[p.status] ?? p.status}
                                    </Badge>
                                </TableCell>
                            </TableRow>
                        ))}
                        {items.data.length === 0 && (
                            <TableRow>
                                <TableCell
                                    colSpan={7}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    Tidak ada barang.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
                <Pagination page={items} />
            </div>
        </>
    );
}

Products.layout = {
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: 'Produk', href: URL },
    ],
};
