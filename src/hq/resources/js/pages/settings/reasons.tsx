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
import { NativeSelect } from '@/components/ui/native-select';
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from '@/components/ui/table';

type Reason = { id: string; type: string; name: string };

type Props = { items: Reason[]; types: Record<string, string> };

const URL = '/pengaturan/reasons';

function ReasonDialog({
    reason,
    type,
    types,
    onClose,
}: {
    reason: Reason | null;
    type: string;
    types: Record<string, string>;
    onClose: () => void;
}) {
    const form = useForm({
        type: reason?.type ?? type,
        name: reason?.name ?? '',
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = { preserveScroll: true, onSuccess: onClose };
        if (reason) {
            form.put(`${URL}/${reason.id}`, options);
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
                            {reason ? 'Ubah Alasan' : 'Tambah Alasan'}
                        </DialogTitle>
                    </DialogHeader>
                    <Field
                        label="Jenis"
                        htmlFor="type"
                        error={form.errors.type}
                    >
                        <NativeSelect
                            id="type"
                            value={form.data.type}
                            onChange={(e) =>
                                form.setData('type', e.target.value)
                            }
                        >
                            {Object.entries(types).map(([v, label]) => (
                                <option key={v} value={v}>
                                    {label}
                                </option>
                            ))}
                        </NativeSelect>
                    </Field>
                    <Field
                        label="Alasan"
                        htmlFor="name"
                        error={form.errors.name}
                    >
                        <Input
                            id="name"
                            value={form.data.name}
                            onChange={(e) =>
                                form.setData('name', e.target.value)
                            }
                            maxLength={100}
                            autoFocus
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

export default function Reasons({ items, types }: Props) {
    const [filter, setFilter] = useState('');
    const [editing, setEditing] = useState<{ reason: Reason | null } | null>(
        null,
    );
    const [deleting, setDeleting] = useState<Reason | null>(null);
    const shown = items.filter((r) => filter === '' || r.type === filter);

    return (
        <>
            <Head title="Alasan" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Alasan"
                    description="Pilihan alasan untuk void penjualan, retur, dan penyesuaian stok di toko."
                    actions={
                        <Button
                            onClick={() => setEditing({ reason: null })}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah
                        </Button>
                    }
                />
                <NativeSelect
                    className="w-56"
                    value={filter}
                    onChange={(e) => setFilter(e.target.value)}
                    aria-label="Jenis"
                >
                    <option value="">Semua jenis</option>
                    {Object.entries(types).map(([v, label]) => (
                        <option key={v} value={v}>
                            {label}
                        </option>
                    ))}
                </NativeSelect>
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-48">Jenis</TableHead>
                            <TableHead>Alasan</TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {shown.map((r) => (
                            <TableRow key={r.id}>
                                <TableCell>{types[r.type] ?? r.type}</TableCell>
                                <TableCell>{r.name}</TableCell>
                                <TableCell className="text-right whitespace-nowrap">
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Ubah"
                                        onClick={() =>
                                            setEditing({ reason: r })
                                        }
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Hapus"
                                        onClick={() => setDeleting(r)}
                                    >
                                        <Trash2 className="size-4" />
                                    </Button>
                                </TableCell>
                            </TableRow>
                        ))}
                        {shown.length === 0 && (
                            <TableRow>
                                <TableCell
                                    colSpan={3}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    Belum ada alasan.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
            </div>
            {editing && (
                <ReasonDialog
                    key={editing.reason?.id ?? 'new'}
                    reason={editing.reason}
                    type={filter || Object.keys(types)[0]}
                    types={types}
                    onClose={() => setEditing(null)}
                />
            )}
            {deleting && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeleting(null)}
                    title="Hapus alasan?"
                    message={`"${deleting.name}" tidak bisa dipilih lagi. Dokumen lama tetap menyimpan alasannya.`}
                    url={`${URL}/${deleting.id}`}
                />
            )}
        </>
    );
}

Reasons.layout = {
    breadcrumbs: [
        { title: 'Pengaturan', href: '/pengaturan' },
        { title: 'Alasan', href: URL },
    ],
};
