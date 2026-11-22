import { Head } from '@inertiajs/react';
import { PlaceholderPattern } from '@/components/ui/placeholder-pattern';

type Props = { title: string; href: string };

/** Menu pages that come with later tasks (Master Data, Pembelian, Stok, Laporan, Pengaturan). */
export default function Placeholder({ title }: Props) {
    return (
        <>
            <Head title={title} />
            <div className="flex h-full flex-1 flex-col gap-4 rounded-xl p-4">
                <h1 className="text-xl font-semibold" data-test="page-title">
                    {title}
                </h1>
                <div className="relative min-h-80 flex-1 overflow-hidden rounded-xl border border-sidebar-border/70 dark:border-sidebar-border">
                    <PlaceholderPattern className="absolute inset-0 size-full stroke-neutral-900/20 dark:stroke-neutral-100/20" />
                    <p className="absolute inset-0 flex items-center justify-center text-sm text-muted-foreground">
                        Halaman {title} sedang disiapkan.
                    </p>
                </div>
            </div>
        </>
    );
}

Placeholder.layout = { breadcrumbs: [] };
