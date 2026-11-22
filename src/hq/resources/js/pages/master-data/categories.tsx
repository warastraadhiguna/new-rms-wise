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
import { NativeSelect } from '@/components/ui/native-select';
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from '@/components/ui/table';
import type { Option, Paginated } from '@/types/master-data';

type Category = {
    id: string;
    name: string;
    parent_id: string | null;
    parent_name: string | null;
    product_count: number;
    version: number;
};

type Props = { q: string; items: Paginated<Category>; parents: Option[] };

const URL = '/master-data/categories';

function CategoryDialog({
    category,
    parents,
    open,
    onOpenChange,
}: {
    category: Category | null;
    parents: Option[];
    open: boolean;
    onOpenChange: (o: boolean) => void;
}) {
    const form = useForm({
        name: category?.name ?? '',
        parent_id: category?.parent_id ?? '',
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = {
            preserveScroll: true,
            onSuccess: () => onOpenChange(false),
        };
        form.transform((data) => ({
            ...data,
            parent_id: data.parent_id === '' ? null : data.parent_id,
        }));
        if (category) {
            form.put(`${URL}/${category.id}`, options);
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
                            {category ? 'Ubah Kategori' : 'Tambah Kategori'}
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
                    <Field
                        label="Induk kategori"
                        htmlFor="parent_id"
                        error={form.errors.parent_id}
                    >
                        <NativeSelect
                            id="parent_id"
                            value={form.data.parent_id}
                            onChange={(e) =>
                                form.setData('parent_id', e.target.value)
                            }
                        >
                            <option value="">— Kategori utama —</option>
                            {parents
                                .filter((p) => p.id !== category?.id)
                                .map((p) => (
                                    <option key={p.id} value={p.id}>
                                        {p.name}
                                    </option>
                                ))}
                        </NativeSelect>
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

export default function Categories({ q, items, parents }: Props) {
    const [editing, setEditing] = useState<Category | null>(null);
    const [formOpen, setFormOpen] = useState(false);
    const [deleting, setDeleting] = useState<Category | null>(null);

    const openForm = (category: Category | null) => {
        setEditing(category);
        setFormOpen(true);
    };

    return (
        <>
            <Head title="Kategori" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Kategori"
                    description="Kategori utama dan sub-kategori (satu tingkat)."
                    actions={
                        <Button
                            onClick={() => openForm(null)}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah Kategori
                        </Button>
                    }
                />
                <SearchBox
                    url={URL}
                    value={q}
                    placeholder="Cari nama kategori"
                />
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead>Nama</TableHead>
                            <TableHead>Induk</TableHead>
                            <TableHead className="w-32 text-right">
                                Jumlah Barang
                            </TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.data.map((c) => (
                            <TableRow key={c.id}>
                                <TableCell
                                    className={
                                        c.parent_id ? 'pl-6' : 'font-medium'
                                    }
                                >
                                    {c.name}
                                </TableCell>
                                <TableCell className="text-muted-foreground">
                                    {c.parent_name ?? '—'}
                                </TableCell>
                                <TableCell className="text-right">
                                    {c.product_count}
                                </TableCell>
                                <TableCell className="text-right">
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        onClick={() => openForm(c)}
                                        title="Ubah"
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        onClick={() => setDeleting(c)}
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
                                    colSpan={4}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    Belum ada kategori.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
                <Pagination page={items} />
            </div>
            {formOpen && (
                <CategoryDialog
                    key={editing?.id ?? 'new'}
                    category={editing}
                    parents={parents}
                    open={formOpen}
                    onOpenChange={setFormOpen}
                />
            )}
            {deleting && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeleting(null)}
                    title="Hapus kategori?"
                    message={`Kategori ${deleting.name} akan dihapus.`}
                    url={`${URL}/${deleting.id}`}
                />
            )}
        </>
    );
}

Categories.layout = {
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: 'Kategori', href: URL },
    ],
};
