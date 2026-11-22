import { Head, Link, router, useForm } from '@inertiajs/react';
import { Package, Pencil, Plus, Search } from 'lucide-react';
import type { FormEvent } from 'react';
import { useState } from 'react';
import {
    Field,
    formatNumber,
    PageHeader,
    Pagination,
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

type Kind = 'customers' | 'suppliers';

export type Contact = {
    id: string;
    code: string;
    name: string;
    company_name: string | null;
    address: string | null;
    email: string | null;
    phone: string | null;
    tax_id: string | null;
    credit_limit: string | null;
    payment_term_days: number | null;
    bank_account: string | null;
    is_customer: number | boolean;
    is_supplier: number | boolean;
    is_active: number | boolean;
    version: number;
};

type Props = {
    kind: Kind;
    q: string;
    active: string;
    items: Paginated<Contact>;
};

const TEXT: Record<Kind, { title: string; one: string; empty: string }> = {
    customers: {
        title: 'Pelanggan',
        one: 'Pelanggan',
        empty: 'Belum ada pelanggan.',
    },
    suppliers: {
        title: 'Supplier',
        one: 'Supplier',
        empty: 'Belum ada supplier.',
    },
};

function ContactDialog({
    kind,
    contact,
    open,
    onOpenChange,
}: {
    kind: Kind;
    contact: Contact | null;
    open: boolean;
    onOpenChange: (o: boolean) => void;
}) {
    const url = `/master-data/${kind}`;
    const form = useForm({
        code: contact?.code ?? '',
        name: contact?.name ?? '',
        company_name: contact?.company_name ?? '',
        address: contact?.address ?? '',
        email: contact?.email ?? '',
        phone: contact?.phone ?? '',
        tax_id: contact?.tax_id ?? '',
        credit_limit: contact?.credit_limit ?? '',
        payment_term_days:
            contact?.payment_term_days === null ||
            contact?.payment_term_days === undefined
                ? ''
                : String(contact.payment_term_days),
        bank_account: contact?.bank_account ?? '',
        is_customer: contact
            ? Boolean(contact.is_customer)
            : kind === 'customers',
        is_supplier: contact
            ? Boolean(contact.is_supplier)
            : kind === 'suppliers',
        is_active: contact ? Boolean(contact.is_active) : true,
    });

    const submit = (e: FormEvent) => {
        e.preventDefault();
        const options = {
            preserveScroll: true,
            onSuccess: () => onOpenChange(false),
        };
        if (contact) {
            form.put(`${url}/${contact.id}`, options);
        } else {
            form.post(url, options);
        }
    };

    const text = (key: keyof typeof form.data, label: string, max: number) => (
        <Field label={label} htmlFor={key} error={form.errors[key]}>
            <Input
                id={key}
                value={String(form.data[key])}
                onChange={(e) => form.setData(key, e.target.value)}
                maxLength={max}
            />
        </Field>
    );

    return (
        <Dialog open={open} onOpenChange={onOpenChange}>
            <DialogContent className="sm:max-w-2xl">
                <form onSubmit={submit} className="grid gap-4">
                    <DialogHeader>
                        <DialogTitle>
                            {contact ? 'Ubah' : 'Tambah'} {TEXT[kind].one}
                        </DialogTitle>
                    </DialogHeader>
                    <div className="grid gap-3 sm:grid-cols-2">
                        <Field
                            label="Kode (kosongkan untuk otomatis)"
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
                                autoFocus
                            />
                        </Field>
                        {text('name', 'Nama', 300)}
                        {text('company_name', 'Nama perusahaan', 200)}
                        {text('phone', 'Telepon / HP', 50)}
                        {text('email', 'Email', 200)}
                        {text('tax_id', 'NPWP', 30)}
                        {text('address', 'Alamat', 2000)}
                        {form.data.is_customer && (
                            <>
                                <Field
                                    label="Limit kredit (kosong = tidak boleh kredit)"
                                    htmlFor="credit_limit"
                                    error={form.errors.credit_limit}
                                >
                                    <Input
                                        id="credit_limit"
                                        inputMode="decimal"
                                        value={form.data.credit_limit}
                                        onChange={(e) =>
                                            form.setData(
                                                'credit_limit',
                                                e.target.value,
                                            )
                                        }
                                    />
                                </Field>
                                <Field
                                    label="Jatuh tempo (hari)"
                                    htmlFor="payment_term_days"
                                    error={form.errors.payment_term_days}
                                >
                                    <Input
                                        id="payment_term_days"
                                        inputMode="numeric"
                                        value={form.data.payment_term_days}
                                        onChange={(e) =>
                                            form.setData(
                                                'payment_term_days',
                                                e.target.value,
                                            )
                                        }
                                    />
                                </Field>
                            </>
                        )}
                        {form.data.is_supplier && (
                            <Field
                                label="Rekening bank"
                                htmlFor="bank_account"
                                error={form.errors.bank_account}
                                className="sm:col-span-2"
                            >
                                <Input
                                    id="bank_account"
                                    value={form.data.bank_account}
                                    onChange={(e) =>
                                        form.setData(
                                            'bank_account',
                                            e.target.value,
                                        )
                                    }
                                    placeholder="BCA 1234567890 a.n. ..."
                                    maxLength={2000}
                                />
                            </Field>
                        )}
                    </div>
                    <div className="flex flex-wrap gap-4 text-sm">
                        <label className="flex items-center gap-2">
                            <Checkbox
                                checked={form.data.is_customer}
                                onCheckedChange={(v) =>
                                    form.setData('is_customer', v === true)
                                }
                                disabled={kind === 'customers'}
                            />
                            Pelanggan
                        </label>
                        <label className="flex items-center gap-2">
                            <Checkbox
                                checked={form.data.is_supplier}
                                onCheckedChange={(v) =>
                                    form.setData('is_supplier', v === true)
                                }
                                disabled={kind === 'suppliers'}
                            />
                            Supplier
                        </label>
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

export default function Contacts({ kind, q, active, items }: Props) {
    const url = `/master-data/${kind}`;
    const [filters, setFilters] = useState({ q, active });
    const [editing, setEditing] = useState<Contact | null>(null);
    const [formOpen, setFormOpen] = useState(false);

    const apply = (next: typeof filters) => {
        setFilters(next);
        router.get(url, next, {
            preserveState: true,
            preserveScroll: true,
            replace: true,
        });
    };

    const openForm = (contact: Contact | null) => {
        setEditing(contact);
        setFormOpen(true);
    };

    const isCustomers = kind === 'customers';

    return (
        <>
            <Head title={TEXT[kind].title} />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title={TEXT[kind].title}
                    description="Kode dikosongkan = dibuat otomatis. Kontak tidak dihapus; jadikan Nonaktif."
                    actions={
                        <Button
                            onClick={() => openForm(null)}
                            data-test="add-button"
                        >
                            <Plus className="size-4" /> Tambah {TEXT[kind].one}
                        </Button>
                    }
                />
                <form
                    onSubmit={(e) => {
                        e.preventDefault();
                        apply(filters);
                    }}
                    className="flex flex-wrap gap-2"
                >
                    <Input
                        className="w-72"
                        value={filters.q}
                        onChange={(e) =>
                            setFilters({ ...filters, q: e.target.value })
                        }
                        placeholder="Cari kode, nama, perusahaan, atau telepon"
                        data-test="search-input"
                    />
                    <NativeSelect
                        className="w-36"
                        value={filters.active}
                        onChange={(e) =>
                            apply({ ...filters, active: e.target.value })
                        }
                        aria-label="Status"
                    >
                        <option value="">Semua status</option>
                        <option value="1">Aktif</option>
                        <option value="0">Nonaktif</option>
                    </NativeSelect>
                    <Button type="submit" variant="secondary">
                        <Search className="size-4" /> Cari
                    </Button>
                </form>
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-32">Kode</TableHead>
                            <TableHead>Nama</TableHead>
                            <TableHead>Telepon</TableHead>
                            {isCustomers ? (
                                <>
                                    <TableHead className="text-right">
                                        Limit kredit
                                    </TableHead>
                                    <TableHead className="text-right">
                                        Jatuh tempo
                                    </TableHead>
                                </>
                            ) : (
                                <TableHead>Rekening</TableHead>
                            )}
                            <TableHead className="w-24">Status</TableHead>
                            <TableHead className="w-24 text-right">
                                Aksi
                            </TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {items.data.map((c) => (
                            <TableRow key={c.id}>
                                <TableCell className="font-mono">
                                    {c.code}
                                </TableCell>
                                <TableCell>
                                    {c.name}
                                    {c.company_name && (
                                        <span className="block text-xs text-muted-foreground">
                                            {c.company_name}
                                        </span>
                                    )}
                                </TableCell>
                                <TableCell>{c.phone}</TableCell>
                                {isCustomers ? (
                                    <>
                                        <TableCell className="text-right">
                                            {c.credit_limit === null
                                                ? '—'
                                                : formatNumber(c.credit_limit)}
                                        </TableCell>
                                        <TableCell className="text-right">
                                            {c.payment_term_days === null
                                                ? '—'
                                                : `${c.payment_term_days} hari`}
                                        </TableCell>
                                    </>
                                ) : (
                                    <TableCell className="max-w-64 truncate">
                                        {c.bank_account}
                                    </TableCell>
                                )}
                                <TableCell>
                                    {c.is_active ? (
                                        <Badge variant="secondary">Aktif</Badge>
                                    ) : (
                                        <Badge variant="outline">
                                            Nonaktif
                                        </Badge>
                                    )}
                                </TableCell>
                                <TableCell className="text-right whitespace-nowrap">
                                    {!isCustomers && (
                                        <Button
                                            variant="ghost"
                                            size="icon"
                                            asChild
                                            title="Salesman dan barang"
                                        >
                                            <Link
                                                href={`/master-data/suppliers/${c.id}`}
                                            >
                                                <Package className="size-4" />
                                            </Link>
                                        </Button>
                                    )}
                                    <Button
                                        variant="ghost"
                                        size="icon"
                                        onClick={() => openForm(c)}
                                        title="Ubah"
                                    >
                                        <Pencil className="size-4" />
                                    </Button>
                                </TableCell>
                            </TableRow>
                        ))}
                        {items.data.length === 0 && (
                            <TableRow>
                                <TableCell
                                    colSpan={7}
                                    className="py-6 text-center text-muted-foreground"
                                >
                                    {TEXT[kind].empty}
                                </TableCell>
                            </TableRow>
                        )}
                    </TableBody>
                </Table>
                <Pagination page={items} />
            </div>
            {formOpen && (
                <ContactDialog
                    key={editing?.id ?? 'new'}
                    kind={kind}
                    contact={editing}
                    open={formOpen}
                    onOpenChange={setFormOpen}
                />
            )}
        </>
    );
}

Contacts.layout = (props: Props) => ({
    breadcrumbs: [
        { title: 'Master Data', href: '/master-data' },
        { title: TEXT[props.kind].title, href: `/master-data/${props.kind}` },
    ],
});
