import { Head, router, useForm } from '@inertiajs/react';
import { Save } from 'lucide-react';
import type { FormEvent } from 'react';
import { useEffect, useState } from 'react';
import { Field, PageHeader } from '@/components/master-data';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import type { BranchOption } from '@/types/master-data';

type Value = string | number | boolean | null;

type Item = {
    key: string;
    field: string;
    label: string;
    help: string | null;
    type: 'string' | 'decimal' | 'integer' | 'boolean' | 'enum';
    min: number | null;
    max: number | null;
    options: Record<string, string>;
    default: Value;
    globalOnly: boolean;
};

type Group = { key: string; label: string; items: Item[] };

type Props = {
    groups: Group[];
    values: Record<
        string,
        { global: Value; branch: Value; overridden: boolean }
    >;
    branches: BranchOption[];
    branchId: string | null;
    receiptWidth: number;
};

const URL = '/pengaturan/settings';

const RECEIPT_FIELDS: Record<string, string> = {
    receipt_store_name: 'store_name',
    receipt_address: 'address',
    receipt_city: 'city',
    receipt_phone: 'phone',
    receipt_footer_1: 'footer_1',
    receipt_footer_2: 'footer_2',
};

/** Form text for a value: booleans as "1"/"0", null as "". */
function text(value: Value): string {
    if (value === null || value === undefined) {
        return '';
    }
    if (typeof value === 'boolean') {
        return value ? '1' : '0';
    }
    return String(value);
}

function show(item: Item, value: Value): string {
    if (item.type === 'boolean') {
        return value ? 'Ya' : 'Tidak';
    }
    if (item.type === 'enum') {
        return item.options[text(value)] ?? text(value);
    }
    return text(value) === '' ? '(kosong)' : text(value);
}

function ReceiptPreview({
    values,
    width,
}: {
    values: Record<string, string>;
    width: number;
}) {
    const [lines, setLines] = useState<string[]>([]);
    const query = new URLSearchParams(
        Object.entries(RECEIPT_FIELDS).map(([field, param]) => [
            param,
            values[field] ?? '',
        ]),
    ).toString();

    useEffect(() => {
        const controller = new AbortController();
        const timer = window.setTimeout(() => {
            fetch(`${URL}/receipt-preview?${query}`, {
                headers: { Accept: 'application/json' },
                signal: controller.signal,
            })
                .then((r) => (r.ok ? r.json() : null))
                .then((body: { lines: string[] } | null) => {
                    if (body) {
                        setLines(body.lines);
                    }
                })
                .catch(() => {});
        }, 250);
        return () => {
            window.clearTimeout(timer);
            controller.abort();
        };
    }, [query]);

    return (
        <div className="overflow-x-auto rounded-md border bg-white p-3 text-black">
            <pre
                className="font-mono text-xs leading-snug"
                style={{ width: `${width}ch` }}
                data-test="receipt-preview"
            >
                {lines.join('\n')}
            </pre>
        </div>
    );
}

export default function Settings({
    groups,
    values,
    branches,
    branchId,
    receiptWidth,
}: Props) {
    const isBranch = branchId !== null;
    const items = groups.flatMap((g) => g.items);

    const form = useForm({
        branch_id: branchId ?? '',
        values: Object.fromEntries(
            items.map((i) => {
                const v = values[i.key];
                const effective =
                    isBranch && v.overridden
                        ? v.branch
                        : (v.global ?? i.default);
                return [i.field, text(effective)];
            }),
        ) as Record<string, string>,
        overrides: Object.fromEntries(
            items.map((i) => [i.field, values[i.key].overridden]),
        ) as Record<string, boolean>,
    });
    const errors = form.errors as Record<string, string | undefined>;

    const submit = (e: FormEvent) => {
        e.preventDefault();
        form.put(URL, { preserveScroll: true });
    };

    const setValue = (field: string, value: string) =>
        form.setData('values', { ...form.data.values, [field]: value });

    const setOverride = (item: Item, on: boolean) => {
        const v = values[item.key];
        form.setData((d) => ({
            ...d,
            overrides: { ...d.overrides, [item.field]: on },
            values: {
                ...d.values,
                [item.field]: text(
                    on
                        ? (v.branch ?? v.global ?? item.default)
                        : (v.global ?? item.default),
                ),
            },
        }));
    };

    /** What the node will use for this key: the branch value when overridden, else global. */
    const effective = (item: Item) =>
        !isBranch || form.data.overrides[item.field]
            ? form.data.values[item.field]
            : text(values[item.key].global ?? item.default);

    const control = (item: Item) => {
        const disabled = isBranch && !form.data.overrides[item.field];
        const value = disabled ? effective(item) : form.data.values[item.field];
        const common = {
            id: item.field,
            disabled,
            'aria-label': item.label,
        };
        if (item.type === 'boolean') {
            return (
                <NativeSelect
                    {...common}
                    value={value}
                    onChange={(e) => setValue(item.field, e.target.value)}
                >
                    <option value="1">Ya</option>
                    <option value="0">Tidak</option>
                </NativeSelect>
            );
        }
        if (item.type === 'enum') {
            return (
                <NativeSelect
                    {...common}
                    value={value}
                    onChange={(e) => setValue(item.field, e.target.value)}
                >
                    {Object.entries(item.options).map(([v, label]) => (
                        <option key={v} value={v}>
                            {label}
                        </option>
                    ))}
                </NativeSelect>
            );
        }
        return (
            <Input
                {...common}
                value={value}
                inputMode={item.type === 'string' ? undefined : 'decimal'}
                maxLength={
                    item.type === 'string' ? (item.max ?? undefined) : 20
                }
                onChange={(e) => setValue(item.field, e.target.value)}
            />
        );
    };

    const renderItem = (item: Item) => {
        if (isBranch && item.globalOnly) {
            return (
                <div key={item.key} className="grid gap-1 text-sm">
                    <span className="font-medium">{item.label}</span>
                    <span className="text-muted-foreground">
                        Hanya global:{' '}
                        {show(item, values[item.key].global ?? item.default)}
                    </span>
                </div>
            );
        }
        const range =
            item.type === 'integer' || item.type === 'decimal'
                ? ` (${item.min ?? 0}–${(item.max ?? 0).toLocaleString('id-ID')})`
                : '';
        return (
            <div key={item.key} className="grid gap-1">
                <Field
                    label={item.label + range}
                    htmlFor={item.field}
                    error={errors[`values.${item.field}`]}
                >
                    {control(item)}
                </Field>
                {isBranch && (
                    <label className="flex items-center gap-2 text-xs text-muted-foreground">
                        <Checkbox
                            checked={form.data.overrides[item.field]}
                            onCheckedChange={(v) =>
                                setOverride(item, v === true)
                            }
                            data-test={`override-${item.field}`}
                        />
                        Timpa untuk cabang ini (global:{' '}
                        {show(item, values[item.key].global ?? item.default)})
                    </label>
                )}
                {item.help && (
                    <p className="text-xs text-muted-foreground">{item.help}</p>
                )}
            </div>
        );
    };

    const receiptValues = Object.fromEntries(
        items
            .filter((i) => i.field in RECEIPT_FIELDS)
            .map((i) => [i.field, effective(i)]),
    );

    return (
        <>
            <Head title="Pengaturan Umum" />
            <form onSubmit={submit} className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Pengaturan Umum"
                    description="Nilai global berlaku untuk semua cabang; cabang bisa menimpa nilai tertentu."
                    actions={
                        <Button
                            type="submit"
                            disabled={form.processing}
                            data-test="save-button"
                        >
                            <Save className="size-4" /> Simpan
                        </Button>
                    }
                />
                <div className="flex items-center gap-2">
                    <label htmlFor="scope" className="text-sm font-medium">
                        Berlaku untuk
                    </label>
                    <NativeSelect
                        id="scope"
                        className="w-72"
                        value={branchId ?? ''}
                        onChange={(e) =>
                            router.get(
                                URL,
                                e.target.value
                                    ? { branch: e.target.value }
                                    : {},
                            )
                        }
                    >
                        <option value="">Global (semua cabang)</option>
                        {branches.map((b) => (
                            <option key={b.id} value={b.id}>
                                Cabang {b.code} - {b.name}
                            </option>
                        ))}
                    </NativeSelect>
                </div>
                {groups.map((group) => (
                    <Card key={group.key}>
                        <CardHeader>
                            <CardTitle>{group.label}</CardTitle>
                        </CardHeader>
                        <CardContent>
                            {group.key === 'receipt' ? (
                                <div className="grid gap-6 lg:grid-cols-[1fr_auto]">
                                    <div className="grid content-start gap-4 sm:grid-cols-2">
                                        {group.items.map(renderItem)}
                                    </div>
                                    <div className="grid content-start gap-2">
                                        <span className="text-sm font-medium">
                                            Pratinjau struk ({receiptWidth}{' '}
                                            karakter)
                                        </span>
                                        <ReceiptPreview
                                            values={receiptValues}
                                            width={receiptWidth}
                                        />
                                    </div>
                                </div>
                            ) : (
                                <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                                    {group.items.map(renderItem)}
                                </div>
                            )}
                        </CardContent>
                    </Card>
                ))}
            </form>
        </>
    );
}

Settings.layout = {
    breadcrumbs: [
        { title: 'Pengaturan', href: '/pengaturan' },
        { title: 'Pengaturan Umum', href: URL },
    ],
};
