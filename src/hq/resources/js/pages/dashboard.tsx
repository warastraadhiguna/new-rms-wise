import { Head, router, usePage } from '@inertiajs/react';
import { useEffect } from 'react';
import { formatNumber, PageHeader } from '@/components/master-data';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from '@/components/ui/table';

type NodeStatus = {
    id: string;
    code: string;
    name: string;
    type: string;
    branch_name: string;
    last_seen_at: string | null;
    minutes_since_seen: number | null;
    pending_outbox: number | null;
    failed_outbox: number | null;
    master_lag: number | null;
    clock_skew_seconds: number | null;
    app_version: string | null;
    alert_minutes: number;
    status: 'green' | 'yellow' | 'red';
    reasons: string[];
};

type SalesToday = {
    node_id: string;
    code: string;
    name: string;
    date: string;
    sale_count: number;
    sales_total: string;
    return_total: string;
    net_total: string;
};

type Props = {
    nodes: NodeStatus[];
    salesToday: SalesToday[];
    checkedAt: string;
};

const REFRESH_MS = 60_000;

const STATUS: Record<
    NodeStatus['status'],
    { label: string; className: string }
> = {
    green: { label: 'Normal', className: 'bg-green-600 text-white' },
    yellow: { label: 'Perlu dicek', className: 'bg-amber-500 text-white' },
    red: { label: 'Terputus', className: 'bg-red-600 text-white' },
};

function seen(node: NodeStatus): string {
    if (node.minutes_since_seen === null) {
        return 'belum pernah';
    }
    if (node.minutes_since_seen < 1) {
        return 'baru saja';
    }
    if (node.minutes_since_seen < 60) {
        return `${node.minutes_since_seen} menit lalu`;
    }
    if (node.minutes_since_seen < 24 * 60) {
        return `${Math.floor(node.minutes_since_seen / 60)} jam lalu`;
    }
    return `${Math.floor(node.minutes_since_seen / (24 * 60))} hari lalu`;
}

function skew(seconds: number | null): string {
    if (seconds === null) {
        return '—';
    }
    if (Math.abs(seconds) < 60) {
        return `${seconds} dtk`;
    }
    return `${Math.round(seconds / 60)} mnt`;
}

export default function Dashboard({ nodes, salesToday, checkedAt }: Props) {
    const { auth } = usePage().props;

    useEffect(() => {
        const timer = window.setInterval(
            () =>
                router.reload({
                    only: ['nodes', 'salesToday', 'checkedAt'],
                }),
            REFRESH_MS,
        );
        return () => window.clearInterval(timer);
    }, []);

    const totals = salesToday.reduce(
        (sum, s) => ({
            count: sum.count + s.sale_count,
            net: sum.net + Number(s.net_total),
        }),
        { count: 0, net: 0 },
    );

    return (
        <>
            <Head title="Dashboard" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title={`Selamat datang${auth.user ? `, ${auth.user.full_name}` : ''}`}
                    description={`Status toko dan gudang, diperbarui otomatis (terakhir ${checkedAt}).`}
                />
                <Card>
                    <CardHeader>
                        <CardTitle>Sinkronisasi toko &amp; gudang</CardTitle>
                    </CardHeader>
                    <CardContent>
                        <Table>
                            <TableHeader>
                                <TableRow>
                                    <TableHead className="w-28">
                                        Status
                                    </TableHead>
                                    <TableHead>Node</TableHead>
                                    <TableHead>Terakhir terlihat</TableHead>
                                    <TableHead className="text-right">
                                        Outbox tertunda
                                    </TableHead>
                                    <TableHead className="text-right">
                                        Ketinggalan versi
                                    </TableHead>
                                    <TableHead className="text-right">
                                        Selisih jam
                                    </TableHead>
                                    <TableHead>Versi aplikasi</TableHead>
                                </TableRow>
                            </TableHeader>
                            <TableBody>
                                {nodes.map((n) => (
                                    <TableRow
                                        key={n.id}
                                        data-test={`node-${n.code}`}
                                    >
                                        <TableCell>
                                            <Badge
                                                className={
                                                    STATUS[n.status].className
                                                }
                                                title={n.reasons.join('\n')}
                                                data-test="node-status"
                                            >
                                                {STATUS[n.status].label}
                                            </Badge>
                                        </TableCell>
                                        <TableCell>
                                            <span className="font-mono">
                                                {n.code}
                                            </span>{' '}
                                            {n.name}
                                            <span className="block text-xs text-muted-foreground">
                                                {n.branch_name}
                                                {n.reasons.length > 0 &&
                                                    ` · ${n.reasons.join(' ')}`}
                                            </span>
                                        </TableCell>
                                        <TableCell>{seen(n)}</TableCell>
                                        <TableCell className="text-right">
                                            {n.pending_outbox ?? '—'}
                                            {n.failed_outbox ? (
                                                <span className="block text-xs text-destructive">
                                                    {n.failed_outbox} ditolak
                                                </span>
                                            ) : null}
                                        </TableCell>
                                        <TableCell className="text-right">
                                            {n.master_lag ?? '—'}
                                        </TableCell>
                                        <TableCell className="text-right">
                                            {skew(n.clock_skew_seconds)}
                                        </TableCell>
                                        <TableCell>
                                            {n.app_version ?? '—'}
                                        </TableCell>
                                    </TableRow>
                                ))}
                                {nodes.length === 0 && (
                                    <TableRow>
                                        <TableCell
                                            colSpan={7}
                                            className="py-6 text-center text-muted-foreground"
                                        >
                                            Belum ada toko atau gudang.
                                        </TableCell>
                                    </TableRow>
                                )}
                            </TableBody>
                        </Table>
                    </CardContent>
                </Card>
                <Card>
                    <CardHeader>
                        <CardTitle>
                            Penjualan hari ini (data yang sudah masuk)
                        </CardTitle>
                    </CardHeader>
                    <CardContent>
                        <Table>
                            <TableHeader>
                                <TableRow>
                                    <TableHead>Toko</TableHead>
                                    <TableHead className="text-right">
                                        Jumlah nota
                                    </TableHead>
                                    <TableHead className="text-right">
                                        Penjualan
                                    </TableHead>
                                    <TableHead className="text-right">
                                        Retur
                                    </TableHead>
                                    <TableHead className="text-right">
                                        Bersih
                                    </TableHead>
                                </TableRow>
                            </TableHeader>
                            <TableBody>
                                {salesToday.map((s) => (
                                    <TableRow
                                        key={s.node_id}
                                        data-test={`sales-${s.code}`}
                                    >
                                        <TableCell>
                                            <span className="font-mono">
                                                {s.code}
                                            </span>{' '}
                                            {s.name}
                                        </TableCell>
                                        <TableCell className="text-right">
                                            {s.sale_count}
                                        </TableCell>
                                        <TableCell className="text-right">
                                            {formatNumber(s.sales_total)}
                                        </TableCell>
                                        <TableCell className="text-right">
                                            {formatNumber(s.return_total)}
                                        </TableCell>
                                        <TableCell className="text-right font-semibold">
                                            {formatNumber(s.net_total)}
                                        </TableCell>
                                    </TableRow>
                                ))}
                                {salesToday.length > 0 && (
                                    <TableRow>
                                        <TableCell className="font-semibold">
                                            Total
                                        </TableCell>
                                        <TableCell className="text-right font-semibold">
                                            {totals.count}
                                        </TableCell>
                                        <TableCell colSpan={2} />
                                        <TableCell className="text-right font-semibold">
                                            {formatNumber(totals.net)}
                                        </TableCell>
                                    </TableRow>
                                )}
                                {salesToday.length === 0 && (
                                    <TableRow>
                                        <TableCell
                                            colSpan={5}
                                            className="py-6 text-center text-muted-foreground"
                                        >
                                            Belum ada toko.
                                        </TableCell>
                                    </TableRow>
                                )}
                            </TableBody>
                        </Table>
                    </CardContent>
                </Card>
            </div>
        </>
    );
}

Dashboard.layout = {
    breadcrumbs: [{ title: 'Dashboard', href: '/dashboard' }],
};
