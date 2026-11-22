import { Head, useForm } from '@inertiajs/react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import {
    ConfirmDelete,
    Field,
    PageHeader,
    Pagination,
    SearchBox,
} from '@/components/master-data';
import { Button } from '@/components/ui/button';
import {
    Dialog,
    DialogContent,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from '@/components/ui/table';
import type { Paginated } from '@/types/master-data';

type Brand = {
    id: string;
    name: string;
    product_count: number;
    version: number;
};

type Props = { q: string; items: Paginated<Brand> };

const URL = '/master-data/brands';

function BrandDialog({
    brand,
    open,
    onOpenChange,
}: {
    brand: Brand | null;
    open: boolean;
    onOpenChange: (o: boolean) => void;
}) {
    const form = useForm({ name: brand?.name ?? '' });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = {
            preserveScroll: true,
            onSuccess: () => onOpenChange(false),
        };
        if (brand) {
            form.put(`${URL}/${brand.id}`, options);
        } else {
            form.post(URL, options);
        }
    };

    return (
        <Dialog open={open} onOpenChange={onOpenChange}>
            <DialogContent>
                <form onSubmit={submit} className="grid gap-4">
                    <DialogHeader>
                        <DialogTitle>
                            {brand ? 'Ubah Merek' : 'Tambah Merek'}
                        </DialogTitle>
                    </DialogHeader>
                    <Field label="Nama" htmlFor="name" error={form.errors.name}>
                        <Input
                            id="name"
                            value={form.data.name}
                            onChange={(e) =>
                                form.setData('name', e.target.value)
                            }
                            maxLength={200}
                            autoFocus
                        />
                    </Field>
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
                            data-test="save-button"
                        >
                            Simpan
                        </Button>
                    </DialogFooter>
                </form>
            </DialogContent>
        </Dialog>
    );
}

export default function Brands({ q, items }: Props) {
    const [editing, setEditing] = useState<Brand | null>(null);
    const [formOpen, setFormOpen] = useState(false);
    const [deleting, setDeleting] = useState<Brand | null>(null);

    const openForm = (brand: Brand | null) => {
        setEditing(brand);
        setFormOpen(true);
    };

    return (
        <>
            <Head title="Merek" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Merek"
                    actions={
                        <Button
                            onClick={() => openForm(null)}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah Merek
                        </Button>
                    }
                />
                <SearchBox url={URL} value={q} placeholder="Cari nama merek" />
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead>Nama</TableHead>
                            <TableHead className="w-32 text-right">
                                Jumlah Barang
                            </TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.data.map((brand) => (
                            <TableRow key={brand.id}>
                                <TableCell>{brand.name}</TableCell>
                                <TableCell className="text-right">
                                    {brand.product_count}
                                </TableCell>
                                <TableCell className="text-right">
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        onClick={() => openForm(brand)}
                                        title="Ubah"
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        onClick={() => setDeleting(brand)}
                                        title="Hapus"
                                    >
                                        <Trash2 className="size-4" />
                                    </Button>
                                </TableCell>
                            </TableRow>
                        ))}
                        {items.data.length === 0 && (
                            <TableRow>
                                <TableCell
                                    colSpan={3}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    Belum ada merek.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
                <Pagination page={items} />
            </div>
            {formOpen && (
                <BrandDialog
                    key={editing?.id ?? 'new'}
                    brand={editing}
                    open={formOpen}
                    onOpenChange={setFormOpen}
                />
            )}
            {deleting && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeleting(null)}
                    title="Hapus merek?"
                    message={`Merek ${deleting.name} akan dihapus.`}
                    url={`${URL}/${deleting.id}`}
                />
            )}
        </>
    );
}

Brands.layout = {
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: 'Merek', href: URL },
    ],
};
