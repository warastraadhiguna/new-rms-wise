import { Head, useForm } from '@inertiajs/react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import { ConfirmDelete, Field, PageHeader } from '@/components/master-data';
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

type Role = {
    id: string;
    code: string;
    name: string;
    permissions: string[];
    version: number;
    users: number;
};

type Group = { group: string; items: { name: string; label: string }[] };

type Props = { roles: Role[]; catalog: Group[]; ownerCode: string };

const URL = '/pengaturan/roles';
const DISCOUNT = 'discount.max:';

function RoleDialog({
    role,
    catalog,
    ownerCode,
    onClose,
}: {
    role: Role | null;
    catalog: Group[];
    ownerCode: string;
    onClose: () => void;
}) {
    const isOwner = role?.code === ownerCode;
    const initial = role?.permissions ?? [];
    const form = useForm({
        code: role?.code ?? '',
        name: role?.name ?? '',
        permissions: initial.filter((p) => !p.startsWith(DISCOUNT)),
        discount_max:
            initial
                .find((p) => p.startsWith(DISCOUNT))
                ?.slice(DISCOUNT.length) ?? '',
    });
    const all = form.data.permissions.includes('*');

    const toggle = (name: string, on: boolean) =>
        form.setData(
            'permissions',
            on
                ? [...form.data.permissions, name]
                : form.data.permissions.filter((p) => p !== name),
        );

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = { preserveScroll: true, onSuccess: onClose };
        if (role) {
            form.put(`${URL}/${role.id}`, options);
        } else {
            form.post(URL, options);
        }
    };

    return (
        <Dialog open onOpenChange={(o) => !o && onClose()}>
            <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
                <form onSubmit={submit} className="grid gap-4">
                    <DialogHeader>
                        <DialogTitle>
                            {role ? 'Ubah Role' : 'Tambah Role'}
                        </DialogTitle>
                    </DialogHeader>
                    <div className="grid gap-3 sm:grid-cols-2">
                        <Field
                            label="Kode"
                            htmlFor="code"
                            error={form.errors.code}
                        >
                            <Input
                                id="code"
                                value={form.data.code}
                                onChange={(e) =>
                                    form.setData(
                                        'code',
                                        e.target.value.toUpperCase(),
                                    )
                                }
                                maxLength={30}
                                disabled={isOwner}
                                autoFocus
                            />
                        </Field>
                        <Field
                            label="Nama"
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
                            />
                        </Field>
                    </div>
                    {isOwner ? (
                        <p className="text-sm text-muted-foreground">
                            Role OWNER selalu punya semua akses dan tidak bisa
                            dihapus.
                        </p>
                    ) : (
                        <>
                            <label className="flex items-center gap-2 text-sm font-medium">
                                <Checkbox
                                    checked={all}
                                    onCheckedChange={(v) =>
                                        toggle('*', v === true)
                                    }
                                    data-test="perm-*"
                                />
                                Semua akses (*)
                            </label>
                            {form.errors.permissions && (
                                <p className="text-sm text-destructive">
                                    {form.errors.permissions}
                                </p>
                            )}
                            {catalog.map((g) => (
                                <fieldset
                                    key={g.group}
                                    className="grid gap-2"
                                    disabled={all}
                                >
                                    <legend className="mb-1 text-sm font-semibold">
                                        {g.group}
                                    </legend>
                                    <div className="grid gap-2 sm:grid-cols-2">
                                        {g.items.map((p) => (
                                            <label
                                                key={p.name}
                                                className="flex items-start gap-2 text-sm"
                                            >
                                                <Checkbox
                                                    className="mt-0.5"
                                                    checked={
                                                        all ||
                                                        form.data.permissions.includes(
                                                            p.name,
                                                        )
                                                    }
                                                    disabled={all}
                                                    onCheckedChange={(v) =>
                                                        toggle(
                                                            p.name,
                                                            v === true,
                                                        )
                                                    }
                                                    data-test={`perm-${p.name}`}
                                                />
                                                <span>
                                                    {p.label}
                                                    <span className="block font-mono text-xs text-muted-foreground">
                                                        {p.name}
                                                    </span>
                                                </span>
                                            </label>
                                        ))}
                                    </div>
                                </fieldset>
                            ))}
                            <Field
                                label="Batas diskon kasir tanpa persetujuan (%) — kosong = tidak boleh diskon"
                                htmlFor="discount_max"
                                error={form.errors.discount_max}
                            >
                                <Input
                                    id="discount_max"
                                    className="w-32"
                                    inputMode="decimal"
                                    value={form.data.discount_max}
                                    onChange={(e) =>
                                        form.setData(
                                            'discount_max',
                                            e.target.value,
                                        )
                                    }
                                    disabled={all}
                                />
                            </Field>
                        </>
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

export default function Roles({ roles, catalog, ownerCode }: Props) {
    const [editing, setEditing] = useState<{ role: Role | null } | null>(null);
    const [deleting, setDeleting] = useState<Role | null>(null);
    const labels = new Map(
        catalog.flatMap((g) => g.items.map((i) => [i.name, i.label] as const)),
    );

    return (
        <>
            <Head title="Role" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Role"
                    description="Hak akses dicentang dari daftar resmi sistem. Berlaku di web pusat dan di semua node."
                    actions={
                        <Button
                            onClick={() => setEditing({ role: null })}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah Role
                        </Button>
                    }
                />
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-40">Kode</TableHead>
                            <TableHead className="w-48">Nama</TableHead>
                            <TableHead>Hak akses</TableHead>
                            <TableHead className="w-24 text-right">
                                Pengguna
                            </TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {roles.map((r) => (
                            <TableRow key={r.id}>
                                <TableCell className="font-mono">
                                    {r.code}
                                </TableCell>
                                <TableCell>{r.name}</TableCell>
                                <TableCell>
                                    <div className="flex flex-wrap gap-1">
                                        {r.permissions.map((p) => (
                                            <Badge
                                                key={p}
                                                variant="outline"
                                                title={labels.get(p) ?? p}
                                            >
                                                {p === '*' ? 'Semua akses' : p}
                                            </Badge>
                                        ))}
                                    </div>
                                </TableCell>
                                <TableCell className="text-right">
                                    {r.users}
                                </TableCell>
                                <TableCell className="text-right whitespace-nowrap">
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Ubah"
                                        onClick={() => setEditing({ role: r })}
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                    {r.code !== ownerCode && (
                                        <Button
                                            variant="ghost"
                                            size="icon"
                                            title="Hapus"
                                            onClick={() => setDeleting(r)}
                                        >
                                            <Trash2 className="size-4" />
                                        </Button>
                                    )}
                                </TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            </div>
            {editing && (
                <RoleDialog
                    key={editing.role?.id ?? 'new'}
                    role={editing.role}
                    catalog={catalog}
                    ownerCode={ownerCode}
                    onClose={() => setEditing(null)}
                />
            )}
            {deleting && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setDeleting(null)}
                    title="Hapus role?"
                    message={`Role ${deleting.code} - ${deleting.name} akan dihapus.`}
                    url={`${URL}/${deleting.id}`}
                />
            )}
        </>
    );
}

Roles.layout = {
    breadcrumbs: [
        { title: 'Pengaturan', href: '/pengaturan' },
        { title: 'Role', href: URL },
    ],
};
