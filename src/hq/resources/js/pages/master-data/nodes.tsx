import { Head, Link, useForm } from '@inertiajs/react';
import { MapPin, Pencil, Plus } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import {
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
    DialogDescription,
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
import type { BranchOption, Paginated } from '@/types/master-data';

type NodeRow = {
    id: string;
    code: string;
    name: string;
    type: string;
    branch_id: string;
    branch_name: string;
    address: string | null;
    is_active: number | boolean;
    locations: number;
};

type Props = {
    q: string;
    items: Paginated<NodeRow>;
    branches: BranchOption[];
    types: Record<string, string>;
};

const URL = '/master-data/nodes';

function NodeDialog({
    node,
    branches,
    types,
    onClose,
}: {
    node: NodeRow | null;
    branches: BranchOption[];
    types: Record<string, string>;
    onClose: () => void;
}) {
    const form = useForm({
        code: node?.code ?? '',
        name: node?.name ?? '',
        type: node?.type ?? 'STORE',
        branch_id: node?.branch_id ?? branches[0]?.id ?? '',
        address: node?.address ?? '',
        is_active: node ? Boolean(node.is_active) : true,
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = { preserveScroll: true, onSuccess: onClose };
        if (node) {
            form.put(`${URL}/${node.id}`, options);
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
                            {node ? 'Ubah Node' : 'Tambah Toko / Gudang'}
                        </DialogTitle>
                        {!node && (
                            <DialogDescription>
                                Toko baru otomatis mendapat lokasi DISPLAY dan
                                DAMAGED; gudang baru STORAGE dan DAMAGED.
                            </DialogDescription>
                        )}
                    </DialogHeader>
                    <Field
                        label="Kode (maks. 10, misalnya TK02)"
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
                            maxLength={10}
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
                            maxLength={200}
                        />
                    </Field>
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
                            disabled={!!node}
                        >
                            {Object.entries(types)
                                .filter(([value]) => node || value !== 'HQ')
                                .map(([value, label]) => (
                                    <option key={value} value={value}>
                                        {label}
                                    </option>
                                ))}
                        </NativeSelect>
                    </Field>
                    <Field
                        label="Cabang"
                        htmlFor="branch_id"
                        error={form.errors.branch_id}
                    >
                        <NativeSelect
                            id="branch_id"
                            value={form.data.branch_id}
                            onChange={(e) =>
                                form.setData('branch_id', e.target.value)
                            }
                        >
                            {branches.map((b) => (
                                <option key={b.id} value={b.id}>
                                    {b.code} - {b.name}
                                </option>
                            ))}
                        </NativeSelect>
                    </Field>
                    <Field
                        label="Alamat"
                        htmlFor="address"
                        error={form.errors.address}
                    >
                        <Input
                            id="address"
                            value={form.data.address}
                            onChange={(e) =>
                                form.setData('address', e.target.value)
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

export default function Nodes({ q, items, branches, types }: Props) {
    const [editing, setEditing] = useState<{ node: NodeRow | null } | null>(
        null,
    );

    return (
        <>
            <Head title="Toko & Gudang" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Toko & Gudang"
                    description="Node tidak dihapus; jadikan Nonaktif. Jenis node tidak bisa diubah."
                    actions={
                        <Button
                            onClick={() => setEditing({ node: null })}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah
                        </Button>
                    }
                />
                <SearchBox
                    url={URL}
                    value={q}
                    placeholder="Cari kode, nama, atau alamat"
                />
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-28">Kode</TableHead>
                            <TableHead>Nama</TableHead>
                            <TableHead className="w-28">Jenis</TableHead>
                            <TableHead>Cabang</TableHead>
                            <TableHead>Alamat</TableHead>
                            <TableHead className="w-20 text-right">
                                Lokasi
                            </TableHead>
                            <TableHead className="w-24">Status</TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.data.map((n) => (
                            <TableRow key={n.id}>
                                <TableCell className="font-mono">
                                    {n.code}
                                </TableCell>
                                <TableCell>{n.name}</TableCell>
                                <TableCell>{types[n.type] ?? n.type}</TableCell>
                                <TableCell>{n.branch_name}</TableCell>
                                <TableCell className="max-w-64 truncate">
                                    {n.address}
                                </TableCell>
                                <TableCell className="text-right">
                                    {n.locations}
                                </TableCell>
                                <TableCell>
                                    {n.is_active ? (
                                        <Badge variant="secondary">Aktif</Badge>
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
                                        asChild
                                        title="Lokasi"
                                    >
                                        <Link href={`${URL}/${n.id}`}>
                                            <MapPin className="size-4" />
                                        </Link>
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Ubah"
                                        onClick={() => setEditing({ node: n })}
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                </TableCell>
                            </TableRow>
                        ))}
                        {items.data.length === 0 && (
                            <TableRow>
                                <TableCell
                                    colSpan={8}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    Belum ada toko atau gudang.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
                <Pagination page={items} />
            </div>
            {editing && (
                <NodeDialog
                    key={editing.node?.id ?? 'new'}
                    node={editing.node}
                    branches={branches}
                    types={types}
                    onClose={() => setEditing(null)}
                />
            )}
        </>
    );
}

Nodes.layout = {
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: 'Toko & Gudang', href: URL },
    ],
};
