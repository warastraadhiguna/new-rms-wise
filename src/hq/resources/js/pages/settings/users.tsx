import { Head, router, useForm, usePage } from '@inertiajs/react';
import { KeyRound, Pencil, Plus, ShieldPlus, X } from 'lucide-react';
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
import type { Paginated } from '@/types/master-data';

type Assignment = {
    id: string;
    role_id: string;
    role_code: string;
    role_name: string;
    node_id: string | null;
    node_code: string | null;
};

type UserRow = {
    id: string;
    username: string;
    full_name: string;
    phone: string | null;
    is_active: number | boolean;
    roles: Assignment[];
};

type Lookup = { id: string; code: string; name: string };

type Props = {
    q: string;
    items: Paginated<UserRow>;
    roles: Lookup[];
    nodes: Lookup[];
    minPassword: number;
};

const URL = '/pengaturan/users';

function PasswordFields({
    form,
    minPassword,
}: {
    form: {
        data: { password: string; password_confirmation: string };
        setData: (k: 'password' | 'password_confirmation', v: string) => void;
        errors: Partial<Record<string, string>>;
    };
    minPassword: number;
}) {
    return (
        <>
            <Field
                label={`Password (minimal ${minPassword} karakter)`}
                htmlFor="password"
                error={form.errors.password}
            >
                <Input
                    id="password"
                    type="password"
                    autoComplete="new-password"
                    value={form.data.password}
                    onChange={(e) => form.setData('password', e.target.value)}
                />
            </Field>
            <Field label="Ulangi password" htmlFor="password_confirmation">
                <Input
                    id="password_confirmation"
                    type="password"
                    autoComplete="new-password"
                    value={form.data.password_confirmation}
                    onChange={(e) =>
                        form.setData('password_confirmation', e.target.value)
                    }
                />
            </Field>
        </>
    );
}

function UserDialog({
    user,
    minPassword,
    onClose,
}: {
    user: UserRow | null;
    minPassword: number;
    onClose: () => void;
}) {
    const form = useForm({
        username: user?.username ?? '',
        full_name: user?.full_name ?? '',
        phone: user?.phone ?? '',
        is_active: user ? Boolean(user.is_active) : true,
        password: '',
        password_confirmation: '',
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = { preserveScroll: true, onSuccess: onClose };
        if (user) {
            form.put(`${URL}/${user.id}`, options);
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
                            {user ? 'Ubah Pengguna' : 'Tambah Pengguna'}
                        </DialogTitle>
                    </DialogHeader>
                    <Field
                        label="Username"
                        htmlFor="username"
                        error={form.errors.username}
                    >
                        <Input
                            id="username"
                            value={form.data.username}
                            onChange={(e) =>
                                form.setData(
                                    'username',
                                    e.target.value.toLowerCase(),
                                )
                            }
                            maxLength={50}
                            autoFocus
                        />
                    </Field>
                    <Field
                        label="Nama lengkap"
                        htmlFor="full_name"
                        error={form.errors.full_name}
                    >
                        <Input
                            id="full_name"
                            value={form.data.full_name}
                            onChange={(e) =>
                                form.setData('full_name', e.target.value)
                            }
                            maxLength={200}
                        />
                    </Field>
                    <Field
                        label="No HP"
                        htmlFor="phone"
                        error={form.errors.phone}
                    >
                        <Input
                            id="phone"
                            value={form.data.phone}
                            onChange={(e) =>
                                form.setData('phone', e.target.value)
                            }
                            maxLength={20}
                        />
                    </Field>
                    {!user && (
                        <PasswordFields form={form} minPassword={minPassword} />
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
                    {form.errors.is_active && (
                        <p
                            className="text-sm text-destructive"
                            data-test="active-error"
                        >
                            {form.errors.is_active}
                        </p>
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

function PasswordDialog({
    user,
    minPassword,
    onClose,
}: {
    user: UserRow;
    minPassword: number;
    onClose: () => void;
}) {
    const form = useForm({ password: '', password_confirmation: '' });

    return (
        <Dialog open onOpenChange={(o) => !o && onClose()}>
            <DialogContent>
                <form
                    onSubmit={(e) => {
                        e.preventDefault();
                        form.put(`${URL}/${user.id}/password`, {
                            preserveScroll: true,
                            onSuccess: onClose,
                        });
                    }}
                    className="grid gap-4"
                >
                    <DialogHeader>
                        <DialogTitle>Ganti Password</DialogTitle>
                        <DialogDescription>
                            Password baru untuk {user.username}. Password lama
                            tidak ditampilkan.
                        </DialogDescription>
                    </DialogHeader>
                    <PasswordFields form={form} minPassword={minPassword} />
                    <DialogFooter>
                        <Button
                            type="button"
                            variant="outline"
                            onClick={onClose}
                        >
                            Batal
                        </Button>
                        <Button type="submit" disabled={form.processing}>
                            Simpan
                        </Button>
                    </DialogFooter>
                </form>
            </DialogContent>
        </Dialog>
    );
}

function RolesDialog({
    user,
    roles,
    nodes,
    onClose,
}: {
    user: UserRow;
    roles: Lookup[];
    nodes: Lookup[];
    onClose: () => void;
}) {
    const form = useForm({ role_id: roles[0]?.id ?? '', node_id: '' });
    const { errors } = usePage().props as { errors?: Record<string, string> };

    return (
        <Dialog open onOpenChange={(o) => !o && onClose()}>
            <DialogContent className="sm:max-w-xl">
                <DialogHeader>
                    <DialogTitle>Role {user.username}</DialogTitle>
                    <DialogDescription>
                        Role untuk semua node, atau hanya untuk satu
                        toko/gudang.
                    </DialogDescription>
                </DialogHeader>
                <div className="flex flex-wrap gap-2">
                    {user.roles.length === 0 && (
                        <span className="text-sm text-muted-foreground">
                            Belum ada role.
                        </span>
                    )}
                    {user.roles.map((a) => (
                        <Badge key={a.id} variant="secondary" className="gap-1">
                            {a.role_name} · {a.node_code ?? 'Semua node'}
                            <button
                                type="button"
                                title="Cabut"
                                onClick={() =>
                                    router.delete(
                                        `${URL}/${user.id}/roles/${a.id}`,
                                        {
                                            preserveScroll: true,
                                        },
                                    )
                                }
                            >
                                <X className="size-3" />
                            </button>
                        </Badge>
                    ))}
                </div>
                {errors?.role && (
                    <p className="text-sm text-destructive">{errors.role}</p>
                )}
                <form
                    onSubmit={(e) => {
                        e.preventDefault();
                        form.post(`${URL}/${user.id}/roles`, {
                            preserveScroll: true,
                        });
                    }}
                    className="flex flex-wrap items-end gap-2"
                >
                    <Field
                        label="Role"
                        htmlFor="role_id"
                        error={form.errors.role_id}
                    >
                        <NativeSelect
                            id="role_id"
                            className="w-48"
                            value={form.data.role_id}
                            onChange={(e) =>
                                form.setData('role_id', e.target.value)
                            }
                        >
                            {roles.map((r) => (
                                <option key={r.id} value={r.id}>
                                    {r.name} ({r.code})
                                </option>
                            ))}
                        </NativeSelect>
                    </Field>
                    <Field
                        label="Node"
                        htmlFor="node_id"
                        error={form.errors.node_id}
                    >
                        <NativeSelect
                            id="node_id"
                            className="w-48"
                            value={form.data.node_id}
                            onChange={(e) =>
                                form.setData('node_id', e.target.value)
                            }
                        >
                            <option value="">Semua node</option>
                            {nodes.map((n) => (
                                <option key={n.id} value={n.id}>
                                    {n.code} - {n.name}
                                </option>
                            ))}
                        </NativeSelect>
                    </Field>
                    <Button type="submit" disabled={form.processing}>
                        <Plus className="size-4" /> Berikan
                    </Button>
                </form>
                <DialogFooter>
                    <Button variant="outline" onClick={onClose}>
                        Tutup
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}

export default function Users({ q, items, roles, nodes, minPassword }: Props) {
    const [editing, setEditing] = useState<{ user: UserRow | null } | null>(
        null,
    );
    const [password, setPassword] = useState<UserRow | null>(null);
    const [rolesFor, setRolesFor] = useState<string | null>(null);
    const rolesUser = items.data.find((u) => u.id === rolesFor) ?? null;

    return (
        <>
            <Head title="Pengguna" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Pengguna"
                    description="Pengguna tidak dihapus; jadikan Nonaktif. Password tidak pernah ditampilkan."
                    actions={
                        <Button
                            onClick={() => setEditing({ user: null })}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah Pengguna
                        </Button>
                    }
                />
                <SearchBox
                    url={URL}
                    value={q}
                    placeholder="Cari username, nama, atau no HP"
                />
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead>Username</TableHead>
                            <TableHead>Nama</TableHead>
                            <TableHead>No HP</TableHead>
                            <TableHead>Role</TableHead>
                            <TableHead className="w-24">Status</TableHead>
                            <TableHead className="w-32 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.data.map((u) => (
                            <TableRow key={u.id}>
                                <TableCell className="font-mono">
                                    {u.username}
                                </TableCell>
                                <TableCell>{u.full_name}</TableCell>
                                <TableCell>{u.phone}</TableCell>
                                <TableCell>
                                    <div className="flex flex-wrap gap-1">
                                        {u.roles.map((a) => (
                                            <Badge key={a.id} variant="outline">
                                                {a.role_code}
                                                {a.node_code
                                                    ? ` @${a.node_code}`
                                                    : ''}
                                            </Badge>
                                        ))}
                                    </div>
                                </TableCell>
                                <TableCell>
                                    {u.is_active ? (
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
                                        title="Role"
                                        onClick={() => setRolesFor(u.id)}
                                    >
                                        <ShieldPlus className="size-4" />
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Ganti password"
                                        onClick={() => setPassword(u)}
                                    >
                                        <KeyRound className="size-4" />
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        title="Ubah"
                                        onClick={() => setEditing({ user: u })}
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                </TableCell>
                            </TableRow>
                        ))}
                        {items.data.length === 0 && (
                            <TableRow>
                                <TableCell
                                    colSpan={6}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    Tidak ada pengguna.
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
                <Pagination page={items} />
            </div>
            {editing && (
                <UserDialog
                    key={editing.user?.id ?? 'new'}
                    user={editing.user}
                    minPassword={minPassword}
                    onClose={() => setEditing(null)}
                />
            )}
            {password && (
                <PasswordDialog
                    user={password}
                    minPassword={minPassword}
                    onClose={() => setPassword(null)}
                />
            )}
            {rolesUser && (
                <RolesDialog
                    user={rolesUser}
                    roles={roles}
                    nodes={nodes}
                    onClose={() => setRolesFor(null)}
                />
            )}
        </>
    );
}

Users.layout = {
    breadcrumbs: [
        { title: 'Pengaturan', href: '/pengaturan' },
        { title: 'Pengguna', href: URL },
    ],
};
