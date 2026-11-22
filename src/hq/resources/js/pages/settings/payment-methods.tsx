import { Head, router, useForm } from '@inertiajs/react';
import { Pencil, Plus } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import { Field, PageHeader } from '@/components/master-data';
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

type Method = {
    id: string;
    code: string;
    name: string;
    is_cash: number | boolean;
    is_credit: number | boolean;
    account_code: string | null;
    is_active: number | boolean;
};

type Props = { items: Method[]; returnCode: string };

const URL = '/pengaturan/payment-methods';

function MethodDialog({
    method,
    returnCode,
    onClose,
}: {
    method: Method | null;
    returnCode: string;
    onClose: () => void;
}) {
    const system = method?.code === returnCode;
    const form = useForm({
        code: method?.code ?? '',
        name: method?.name ?? '',
        is_cash: Boolean(method?.is_cash),
        is_credit: Boolean(method?.is_credit),
        account_code: method?.account_code ?? '',
        is_active: method ? Boolean(method.is_active) : true,
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = { preserveScroll: true, onSuccess: onClose };
        if (method) {
            form.put(`${URL}/${method.id}`, options);
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
                            {method
                                ? 'Ubah Metode Bayar'
                                : 'Tambah Metode Bayar'}
                        </DialogTitle>
                    </DialogHeader>
                    {system && (
                        <p className="text-sm text-muted-foreground">
                            RETUR dipakai sistem untuk Tukar Barang; hanya
                            namanya yang bisa diubah.
                        </p>
                    )}
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
                            disabled={system}
                            autoFocus
                        />
                    </Field>
                    <Field
                        label="Nama (tampil di kasir dan struk)"
                        htmlFor="name"
                        error={form.errors.name}
                    >
                        <Input
                            id="name"
                            value={form.data.name}
                            onChange={(e) =>
                                form.setData('name', e.target.value)
                            }
                            maxLength={50}
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
                    {!system && (
                        <div className="grid gap-2 text-sm">
                            <label className="flex items-center gap-2">
                                <Checkbox
                                    checked={form.data.is_cash}
                                    onCheckedChange={(v) =>
                                        form.setData((d) => ({
                                            ...d,
                                            is_cash: v === true,
                                            is_credit:
                                                v === true
                                                    ? false
                                                    : d.is_credit,
                                        }))
                                    }
                                />
                                Tunai (masuk laci kas)
                            </label>
                            <label className="flex items-center gap-2">
                                <Checkbox
                                    checked={form.data.is_credit}
                                    onCheckedChange={(v) =>
                                        form.setData((d) => ({
                                            ...d,
                                            is_credit: v === true,
                                            is_cash:
                                                v === true ? false : d.is_cash,
                                        }))
                                    }
                                />
                                Kredit (menjadi piutang)
                            </label>
                            {form.errors.is_credit && (
                                <p className="text-xs text-destructive">
                                    {form.errors.is_credit}
                                </p>
                            )}
                            <label className="flex items-center gap-2">
                                <Checkbox
                                    checked={form.data.is_active}
                                    onCheckedChange={(v) =>
                                        form.setData('is_active', v === true)
                                    }
                                />
                                Aktif
                            </label>
                        </div>
                    )}
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

export default function PaymentMethods({ items, returnCode }: Props) {
    const [editing, setEditing] = useState<{ method: Method | null } | null>(
        null,
    );
    const hasReturn = items.some((m) => m.code === returnCode);

    return (
        <>
            <Head title="Metode Bayar" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Metode Bayar"
                    description="Metode bayar tidak dihapus karena dipakai transaksi; jadikan Nonaktif."
                    actions={
                        <Button
                            onClick={() => setEditing({ method: null })}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah
                        </Button>
                    }
                />
                {!hasReturn && (
                    <div className="flex items-center justify-between rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-950 dark:text-amber-100">
                        Metode RETUR (untuk Tukar Barang) belum ada.
                        <Button
                            size="sm"
                            onClick={() =>
                                router.post(
                                    `${URL}/return`,
                                    {},
                                    { preserveScroll: true },
                                )
                            }
                        >
                            Buat RETUR
                        </Button>
                    </div>
                )}
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-32">Kode</TableHead>
                            <TableHead>Nama</TableHead>
                            <TableHead>Jenis</TableHead>
                            <TableHead>Kode akun</TableHead>
                            <TableHead className="w-24">Status</TableHead>
                            <TableHead className="w-16 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.map((m) => (
                            <TableRow key={m.id}>
                                <TableCell className="font-mono">
                                    {m.code}
                                </TableCell>
                                <TableCell>{m.name}</TableCell>
                                <TableCell>
                                    {m.code === returnCode
                                        ? 'Sistem (Tukar Barang)'
                                        : m.is_cash
                                          ? 'Tunai'
                                          : m.is_credit
                                            ? 'Kredit'
                                            : 'Non-tunai'}
                                </TableCell>
                                <TableCell>{m.account_code}</TableCell>
                                <TableCell>
                                    {m.is_active ? (
                                        <Badge variant="secondary">Aktif</Badge>
                                    ) : (
                                        <Badge variant="outline">
                                            Nonaktif
                                        </Badge>
                                    )}
                                </TableCell>
                                <TableCell className="text-right">
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Ubah"
                                        onClick={() =>
                                            setEditing({ method: m })
                                        }
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                </TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            </div>
            {editing && (
                <MethodDialog
                    key={editing.method?.id ?? 'new'}
                    method={editing.method}
                    returnCode={returnCode}
                    onClose={() => setEditing(null)}
                />
            )}
        </>
    );
}

PaymentMethods.layout = {
    breadcrumbs: [
        { title: 'Pengaturan', href: '/pengaturan' },
        { title: 'Metode Bayar', href: URL },
    ],
};
