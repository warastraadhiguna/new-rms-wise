import { Link, router, usePage } from '@inertiajs/react';
import { ChevronLeft, ChevronRight, Search } from 'lucide-react';
import type { FormEvent, ReactNode } from 'react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import type { Paginated } from '@/types/master-data';

/** Indonesian number with dot thousands, at most 4 decimals ("12.500", "0,25"). */
export function formatNumber(
    value: string | number | null | undefined,
): string {
    if (value === null || value === undefined || value === '') {
        return '';
    }
    return Number(value).toLocaleString('id-ID', { maximumFractionDigits: 4 });
}

/** Page title with optional actions on the right. */
export function PageHeader({
    title,
    description,
    actions,
}: {
    title: string;
    description?: string;
    actions?: ReactNode;
}) {
    return (
        <div className="flex flex-wrap items-end justify-between gap-3">
            <div>
                <h1 className="text-xl font-semibold" data-test="page-title">
                    {title}
                </h1>
                {description && (
                    <p className="text-sm text-muted-foreground">
                        {description}
                    </p>
                )}
            </div>
            {actions && <div className="flex gap-2">{actions}</div>}
        </div>
    );
}

/** GET search box: keeps the other query parameters, starts at page 1. */
export function SearchBox({
    url,
    value,
    placeholder,
    extra = {},
}: {
    url: string;
    value: string;
    placeholder: string;
    extra?: Record<string, string>;
}) {
    const [q, setQ] = useState(value);

    const submit = (e: FormEvent) => {
        e.preventDefault();
        router.get(
            url,
            { ...extra, q },
            { preserveState: true, preserveScroll: true, replace: true },
        );
    };

    return (
        <form onSubmit={submit} className="flex w-full max-w-md gap-2">
            <Input
                name="q"
                value={q}
                onChange={(e) => setQ(e.target.value)}
                placeholder={placeholder}
                data-test="search-input"
            />
            <Button type="submit" variant="secondary">
                <Search className="size-4" />
                Cari
            </Button>
        </form>
    );
}

/** "Menampilkan 1–25 dari 120" with previous / next. */
export function Pagination<T>({ page }: { page: Paginated<T> }) {
    const prev = page.links[0]?.url ?? null;
    const next = page.links[page.links.length - 1]?.url ?? null;

    return (
        <div className="flex items-center justify-between text-sm text-muted-foreground">
            <span>
                {page.total === 0
                    ? 'Tidak ada data'
                    : `Menampilkan ${page.from}–${page.to} dari ${page.total.toLocaleString('id-ID')}`}
            </span>
            <div className="flex gap-1">
                <Button
                    variant="outline"
                    size="sm"
                    disabled={!prev}
                    asChild={!!prev}
                >
                    {prev ? (
                        <Link href={prev} preserveScroll preserveState>
                            <ChevronLeft className="size-4" /> Sebelumnya
                        </Link>
                    ) : (
                        <span>
                            <ChevronLeft className="size-4" /> Sebelumnya
                        </span>
                    )}
                </Button>
                <Button
                    variant="outline"
                    size="sm"
                    disabled={!next}
                    asChild={!!next}
                >
                    {next ? (
                        <Link href={next} preserveScroll preserveState>
                            Berikutnya <ChevronRight className="size-4" />
                        </Link>
                    ) : (
                        <span>
                            Berikutnya <ChevronRight className="size-4" />
                        </span>
                    )}
                </Button>
            </div>
        </div>
    );
}

/** Asks before a soft delete; shows the server's refusal (errors.delete) if any. */
export function ConfirmDelete({
    open,
    onOpenChange,
    title,
    message,
    url,
    deactivateUrl,
}: {
    open: boolean;
    onOpenChange: (open: boolean) => void;
    title: string;
    message: string;
    url: string;
    /** When the server refuses because the record is in use (errors.delete_used): offer "Jadikan Nonaktif". */
    deactivateUrl?: string;
}) {
    const [processing, setProcessing] = useState(false);
    const { errors } = usePage().props as { errors?: Record<string, string> };

    const remove = () => {
        setProcessing(true);
        router.delete(url, {
            preserveScroll: true,
            onSuccess: () => onOpenChange(false),
            onFinish: () => setProcessing(false),
        });
    };

    return (
        <Dialog open={open} onOpenChange={onOpenChange}>
            <DialogContent>
                <DialogHeader>
                    <DialogTitle>{title}</DialogTitle>
                    <DialogDescription>{message}</DialogDescription>
                </DialogHeader>
                {errors?.delete && (
                    <p
                        className="text-sm text-destructive"
                        data-test="delete-error"
                    >
                        {errors.delete}
                    </p>
                )}
                <DialogFooter>
                    <Button
                        variant="outline"
                        onClick={() => onOpenChange(false)}
                    >
                        Batal
                    </Button>
                    {deactivateUrl && errors?.delete_used && (
                        <Button
                            variant="secondary"
                            onClick={() =>
                                router.post(
                                    deactivateUrl,
                                    {},
                                    {
                                        preserveScroll: true,
                                        onSuccess: () => onOpenChange(false),
                                    },
                                )
                            }
                            data-test="deactivate-button"
                        >
                            Jadikan Nonaktif
                        </Button>
                    )}
                    <Button
                        variant="destructive"
                        onClick={remove}
                        disabled={processing}
                        data-test="confirm-delete"
                    >
                        Hapus
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}

/** Label + control + error in one block for dense forms. */
export function Field({
    label,
    error,
    children,
    htmlFor,
    className,
}: {
    label: string;
    error?: string;
    children: ReactNode;
    htmlFor?: string;
    className?: string;
}) {
    return (
        <div className={`grid gap-1 ${className ?? ''}`}>
            <label htmlFor={htmlFor} className="text-sm font-medium">
                {label}
            </label>
            {children}
            {error && <p className="text-xs text-destructive">{error}</p>}
        </div>
    );
}
