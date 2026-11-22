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
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
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

type Unit = {
    id: string;
    code: string;
    name: string;
    allow_decimal: number | boolean;
    version: number;
};

type Props = { q: string; items: Paginated<Unit> };

const URL = '/master-data/units';

function UnitDialog({
    unit,
    open,
    onOpenChange,
}: {
    unit: Unit | null;
    open: boolean;
    onOpenChange: (o: boolean) => void;
}) {
    const form = useForm({
        code: unit?.code ?? '',
        name: unit?.name ?? '',
        allow_decimal: Boolean(unit?.allow_decimal),
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = {
            preserveScroll: true,
            onSuccess: () => onOpenChange(false),
        };
        if (unit) {
            form.put(`${URL}/${unit.id}`, options);
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
                            {unit ? 'Ubah Satuan' : 'Tambah Satuan'}
                        </DialogTitle>
                    </DialogHeader>
                    <Field label="Kode" htmlFor="code" error={form.errors.code}>
                        <Input
                            id="code"
                            value={form.data.code}
                            onChange={(e) =>
                                form.setData(
                                    'code',
                                    e.target.value.toUpperCase(),
                                )
                            }
                            maxLength={20}
                            autoFocus
                        />
                    </Field>
                    <Field label="Nama" htmlFor="name" error={form.errors.name}>
                        <Input
                            id="name"
                            value={form.data.name}
                            onChange={(e) =>
                                form.setData('name', e.target.value)
                            }
                            maxLength={50}
                        />
                    </Field>
                    <label className="flex items-center gap-2 text-sm">
                        <Checkbox
                            checked={form.data.allow_decimal}
                            onCheckedChange={(v) =>
                                form.setData('allow_decimal', v === true)
                            }
                        />
                        Boleh desimal (misalnya KG 0,25)
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

export default function Units({ q, items }: Props) {
    const [editing, setEditing] = useState<Unit | null>(null);
    const [formOpen, setFormOpen] = useState(false);
    const [deleting, setDeleting] = useState<Unit | null>(null);

    const openForm = (unit: Unit | null) => {
        setEditing(unit);
        setFormOpen(true);
    };

    return (
        <>
            <Head title="Satuan" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Satuan"
                    description="Satuan barang. Kode yang pernah dipakai tidak bisa dipakai lagi."
                    actions={
                        <Button
                            onClick={() => openForm(null)}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah Satuan
                        </Button>
                    }
                />
                <SearchBox
                    url={URL}
                    value={q}
                    placeholder="Cari kode atau nama satuan"
                />
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-32">Kode</TableHead>
                            <TableHead>Nama</TableHead>
                            <TableHead className="w-32">Desimal</TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.data.map((unit) => (
                            <TableRow key={unit.id}>
                                <TableCell className="font-mono">
                                    {unit.code}
                                </TableCell>
                                <TableCell>{unit.name}</TableCell>
                                <TableCell>
                                    {unit.allow_decimal ? (
                                        <Badge variant="secondary">Boleh</Badge>
                                    ) : (
                                        '—'
                                    )}
                                </TableCell>
                                <TableCell className="text-right">
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        onClick={() => openForm(unit)}
                                        title="Ubah"
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        onClick={() => setDeleting(unit)}
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
                                    Belum ada satuan.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
                <Pagination page={items} />
            </div>
            {formOpen && (
                <UnitDialog
                    key={editing?.id ?? 'new'}
                    unit={editing}
                    open={formOpen}
                    onOpenChange={setFormOpen}
                />
            )}
            {deleting && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeleting(null)}
                    title="Hapus satuan?"
                    message={`Satuan ${deleting.code} - ${deleting.name} akan dihapus.`}
                    url={`${URL}/${deleting.id}`}
                />
            )}
        </>
    );
}

Units.layout = {
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: 'Satuan', href: URL },
    ],
};
