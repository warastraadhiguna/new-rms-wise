import { Head, useForm } from '@inertiajs/react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import { ConfirmDelete, Field, PageHeader } from '@/components/master-data';
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

type ExpenseType = { id: string; name: string; account_code: string | null };

type Props = { items: ExpenseType[] };

const URL = '/pengaturan/expense-types';

function ExpenseTypeDialog({
    item,
    onClose,
}: {
    item: ExpenseType | null;
    onClose: () => void;
}) {
    const form = useForm({
        name: item?.name ?? '',
        account_code: item?.account_code ?? '',
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = { preserveScroll: true, onSuccess: onClose };
        if (item) {
            form.put(`${URL}/${item.id}`, options);
        } else {
            form.post(URL, options);
        }
    };

    return (
        <Dialog open onOpenChange={(o) => !o && onClose()}>
            <DialogContent>
                <form onSubmit={submit} className="grid gap-4">
                    <DialogHeader>
                        <DialogTitle>
                            {item ? 'Ubah Jenis Biaya' : 'Tambah Jenis Biaya'}
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
                        label="Kode akun (opsional)"
                        htmlFor="account_code"
                        error={form.errors.account_code}
                    >
                        <Input
                            id="account_code"
                            value={form.data.account_code}
                            onChange={(e) =>
                                form.setData('account_code', e.target.value)
                            }
                            maxLength={20}
                        />
                    </Field>
                    <DialogFooter>
                        <Button
                            type="button"
                            variant="outline"
                            onClick={onClose}
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

export default function ExpenseTypes({ items }: Props) {
    const [editing, setEditing] = useState<{ item: ExpenseType | null } | null>(
        null,
    );
    const [deleting, setDeleting] = useState<ExpenseType | null>(null);

    return (
        <>
            <Head title="Jenis Biaya" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Jenis Biaya"
                    description="Jenis pengeluaran kas yang bisa dicatat di toko."
                    actions={
                        <Button
                            onClick={() => setEditing({ item: null })}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah
                        </Button>
                    }
                />
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead>Nama</TableHead>
                            <TableHead className="w-48">Kode akun</TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.map((t) => (
                            <TableRow key={t.id}>
                                <TableCell>{t.name}</TableCell>
                                <TableCell>{t.account_code}</TableCell>
                                <TableCell className="text-right whitespace-nowrap">
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Ubah"
                                        onClick={() => setEditing({ item: t })}
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Hapus"
                                        onClick={() => setDeleting(t)}
                                    >
                                        <Trash2 className="size-4" />
                                    </Button>
                                </TableCell>
                            </TableRow>
                        ))}
                        {items.length === 0 && (
                            <TableRow>
                                <TableCell
                                    colSpan={3}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    Belum ada jenis biaya.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
            </div>
            {editing && (
                <ExpenseTypeDialog
                    key={editing.item?.id ?? 'new'}
                    item={editing.item}
                    onClose={() => setEditing(null)}
                />
            )}
            {deleting && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeleting(null)}
                    title="Hapus jenis biaya?"
                    message={`"${deleting.name}" tidak bisa dipilih lagi. Pengeluaran lama tetap menyimpannya.`}
                    url={`${URL}/${deleting.id}`}
                />
            )}
        </>
    );
}

ExpenseTypes.layout = {
    breadcrumbs: [
        { title: 'Pengaturan', href: '/pengaturan' },
        { title: 'Jenis Biaya', href: URL },
    ],
};
