import { Head, Link, usePage } from '@inertiajs/react';
import type { LucideIcon } from 'lucide-react';
import {
    CreditCard,
    MessageSquareText,
    Receipt,
    ShieldCheck,
    SlidersHorizontal,
    Users,
} from 'lucide-react';
import { PageHeader } from '@/components/master-data';
import {
    Card,
    CardDescription,
    CardHeader,
    CardTitle,
} from '@/components/ui/card';
import type { Auth } from '@/types';

type Section = {
    title: string;
    description: string;
    href: string;
    icon: LucideIcon;
    permission: string;
};

export const settingsSections: Section[] = [
    {
        title: 'Pengguna',
        description: 'Akun, password, dan role per node.',
        href: '/pengaturan/users',
        icon: Users,
        permission: 'users.manage',
    },
    {
        title: 'Role',
        description: 'Hak akses yang bisa diberikan ke pengguna.',
        href: '/pengaturan/roles',
        icon: ShieldCheck,
        permission: 'users.manage',
    },
    {
        title: 'Pengaturan Umum',
        description:
            'Struk, penjualan, stok, dan shift (global atau per cabang).',
        href: '/pengaturan/settings',
        icon: SlidersHorizontal,
        permission: 'settings.manage',
    },
    {
        title: 'Metode Bayar',
        description: 'Tunai, kredit, non-tunai, dan RETUR.',
        href: '/pengaturan/payment-methods',
        icon: CreditCard,
        permission: 'settings.manage',
    },
    {
        title: 'Alasan',
        description: 'Alasan void, retur, dan penyesuaian stok.',
        href: '/pengaturan/reasons',
        icon: MessageSquareText,
        permission: 'settings.manage',
    },
    {
        title: 'Jenis Biaya',
        description: 'Jenis pengeluaran kas toko.',
        href: '/pengaturan/expense-types',
        icon: Receipt,
        permission: 'settings.manage',
    },
];

export default function SettingsIndex() {
    const { auth } = usePage<{ auth: Auth }>().props;
    const can = (p: string) =>
        auth.permissions.includes('*') || auth.permissions.includes(p);
    const visible = settingsSections.filter((s) => can(s.permission));

    return (
        <>
            <Head title="Pengaturan" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader title="Pengaturan" />
                {visible.length === 0 ? (
                    <p className="text-sm text-muted-foreground">
                        Anda belum punya akses ke halaman pengaturan.
                    </p>
                ) : (
                    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                        {visible.map((s) => (
                            <Link key={s.href} href={s.href} prefetch>
                                <Card className="h-full transition-colors hover:bg-muted/40">
                                    <CardHeader>
                                        <s.icon className="size-5 text-muted-foreground" />
                                        <CardTitle>{s.title}</CardTitle>
                                        <CardDescription>
                                            {s.description}
                                        </CardDescription>
                                    </CardHeader>
                                </Card>
                            </Link>
                        ))}
                    </div>
                )}
            </div>
        </>
    );
}

SettingsIndex.layout = {
    breadcrumbs: [{ title: 'Pengaturan', href: '/pengaturan' }],
};
