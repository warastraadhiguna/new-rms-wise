import { Head, router, useForm, usePage } from '@inertiajs/react';
import { KeyRound, Pencil, Plus } from 'lucide-react';
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
import { NativeSelect } from '@/components/ui/native-select';
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from '@/components/ui/table';

type Location = {
    id: string;
    code: string;
    name: string;
    type: string;
    is_sellable: number | boolean;
    is_active: number | boolean;
};

type Props = {
    node: {
        id: string;
        code: string;
        name: string;
        type: string;
        branch_name: string;
        address: string | null;
        is_active: number | boolean;
    };
    locations: Location[];
    types: Record<string, string>;
    locationTypes: Record<string, string>;
    syncToken: SyncToken | null;
};

type SyncToken = {
    id: number;
    token_hint: string;
    created_at: string;
    last_used_at: string | null;
};

const LIST = '/master-data/nodes';

function LocationDialog({
    base,
    location,
    locationTypes,
    onClose,
}: {
    base: string;
    location: Location | null;
    locationTypes: Record<string, string>;
    onClose: () => void;
}) {
    const form = useForm({
        code: location?.code ?? '',
        name: location?.name ?? '',
        type: location?.type ?? 'STORAGE',
        is_sellable: location ? Boolean(location.is_sellable) : false,
        is_active: location ? Boolean(location.is_active) : true,
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = { preserveScroll: true, onSuccess: onClose };
        if (location) {
            form.put(`${base}/locations/${location.id}`, options);
        } else {
            form.post(`${base}/locations`, options);
        }
    };

    return (
        <Dialog open onOpenChange={(o) => !o && onClose()}>
            <DialogContent>
                <form onSubmit={submit} className="grid gap-4">
                    <DialogHeader>
                        <DialogTitle>
                            {location ? 'Ubah Lokasi' : 'Tambah Lokasi'}
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
                            maxLength={100}
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
                            onChange={(e) => {
                                form.setData((d) => ({
                                    ...d,
                                    type: e.target.value,
                                    is_sellable:
                                        e.target.value === 'DAMAGED'
                                            ? false
                                            : d.is_sellable,
                                }));
                            }}
                        >
                            {Object.entries(locationTypes).map(
                                ([value, label]) => (
                                    <option key={value} value={value}>
                                        {label}
                                    </option>
                                ),
                            )}
                        </NativeSelect>
                    </Field>
                    <label className="flex items-center gap-2 text-sm">
                        <Checkbox
                            checked={form.data.is_sellable}
                            disabled={form.data.type === 'DAMAGED'}
                            onCheckedChange={(v) =>
                                form.setData('is_sellable', v === true)
                            }
                        />
                        Bisa dijual (kasir mengambil stok dari lokasi ini)
                    </label>
                    {form.errors.is_sellable && (
                        <p className="text-sm text-destructive">
                            {form.errors.is_sellable}
                        </p>
                    )}
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

/** Sync token: created here, shown once (flash), revocable. */
function SyncTokenPanel({
    nodeId,
    token,
}: {
    nodeId: string;
    token: SyncToken | null;
}) {
    const page = usePage();
    const plain = (page.flash as { sync_token?: string } | undefined)
        ?.sync_token;
    const [confirmRevoke, setConfirmRevoke] = useState(false);
    const url = `${LIST}/${nodeId}/sync-token`;

    return (
        <div className="grid gap-2 rounded-md border p-3 text-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
                <div>
                    <span className="font-medium">Token sinkron: </span>
                    {token ? (
                        <span data-test="token-status">
                            aktif ({token.token_hint}), dibuat{' '}
                            {token.created_at.slice(0, 16)} UTC
                            {token.last_used_at
                                ? `, terakhir dipakai ${token.last_used_at.slice(0, 16)} UTC`
                                : ', belum pernah dipakai'}
                        </span>
                    ) : (
                        <span
                            className="text-muted-foreground"
                            data-test="token-status"
                        >
                            belum ada — node ini belum bisa sinkron
                        </span>
                    )}
                </div>
                <div className="flex gap-2">
                    <Button
                        size="sm"
                        variant={token ? 'outline' : 'default'}
                        onClick={() =>
                            router.post(url, {}, { preserveScroll: true })
                        }
                        data-test="create-token"
                    >
                        <KeyRound className="size-4" /> Buat token sinkron
                    </Button>
                    {token && (
                        <Button
                            size="sm"
                            variant="destructive"
                            onClick={() => setConfirmRevoke(true)}
                        >
                            Cabut
                        </Button>
                    )}
                </div>
            </div>
            {plain && (
                <div className="grid gap-1 rounded-md bg-amber-50 p-2 text-amber-900 dark:bg-amber-950 dark:text-amber-100">
                    <span>
                        Salin token ini ke konfigurasi Node Service sekarang.
                        Token tidak akan ditampilkan lagi; token lama sudah
                        tidak berlaku.
                    </span>
                    <code
                        className="font-mono text-sm break-all select-all"
                        data-test="sync-token"
                    >
                        {plain}
                    </code>
                </div>
            )}
            {confirmRevoke && (
                <ConfirmDelete
                    open
                    onOpenChange={(o) => !o && setConfirmRevoke(false)}
                    title="Cabut token sinkron?"
                    message="Node ini tidak bisa sinkron lagi sampai diberi token baru."
                    url={url}
                />
            )}
        </div>
    );
}

export default function NodePage({
    node,
    locations,
    types,
    locationTypes,
    syncToken,
}: Props) {
    const base = `${LIST}/${node.id}`;
    const [editing, setEditing] = useState<{
        location: Location | null;
    } | null>(null);

    return (
        <>
            <Head title={`${node.code} - Lokasi`} />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title={`${node.code} - ${node.name}`}
                    description={[
                        types[node.type] ?? node.type,
                        node.branch_name,
                        node.address,
                    ]
                        .filter(Boolean)
                        .join(' · ')}
                    actions={
                        <Button
                            onClick={() => setEditing({ location: null })}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah Lokasi
                        </Button>
                    }
                />
                {node.type !== 'HQ' && (
                    <SyncTokenPanel nodeId={node.id} token={syncToken} />
                )}
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-32">Kode</TableHead>
                            <TableHead>Nama</TableHead>
                            <TableHead>Jenis</TableHead>
                            <TableHead className="w-28">Bisa dijual</TableHead>
                            <TableHead className="w-24">Status</TableHead>
                            <TableHead className="w-16 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {locations.map((l) => (
                            <TableRow key={l.id}>
                                <TableCell className="font-mono">
                                    {l.code}
                                </TableCell>
                                <TableCell>{l.name}</TableCell>
                                <TableCell>
                                    {locationTypes[l.type] ?? l.type}
                                </TableCell>
                                <TableCell>
                                    {l.is_sellable ? (
                                        <Badge variant="secondary">Ya</Badge>
                                    ) : (
                                        '—'
                                    )}
                                </TableCell>
                                <TableCell>
                                    {l.is_active ? (
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
                                            setEditing({ location: l })
                                        }
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                </TableCell>
                            </TableRow>
                        ))}
                        {locations.length === 0 && (
                            <TableRow>
                                <TableCell
                                    colSpan={6}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    Belum ada lokasi.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
            </div>
            {editing && (
                <LocationDialog
                    key={editing.location?.id ?? 'new'}
                    base={base}
                    location={editing.location}
                    locationTypes={locationTypes}
                    onClose={() => setEditing(null)}
                />
            )}
        </>
    );
}

NodePage.layout = {
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: 'Toko & Gudang', href: LIST },
    ],
};
