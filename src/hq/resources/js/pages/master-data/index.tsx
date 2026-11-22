import { Head, Link } from '@inertiajs/react';
import {
    Boxes,
    Package,
    Ruler,
    Store,
    Tags,
    Truck,
    UserRound,
} from 'lucide-react';
import { PageHeader } from '@/components/master-data';
import {
    Card,
    CardDescription,
    CardHeader,
    CardTitle,
} from '@/components/ui/card';

const sections = [
    {
        title: 'Produk',
        description: 'Barang, satuan jual, barcode, dan harga.',
        href: '/master-data/products',
        icon: Package,
    },
    {
        title: 'Satuan',
        description: 'PCS, BOX, KG, dan satuan lain.',
        href: '/master-data/units',
        icon: Ruler,
    },
    {
        title: 'Kategori',
        description: 'Pengelompokan barang (jenis barang).',
        href: '/master-data/categories',
        icon: Boxes,
    },
    {
        title: 'Merek',
        description: 'Merek barang.',
        href: '/master-data/brands',
        icon: Tags,
    },
    {
        title: 'Pelanggan',
        description: 'Pelanggan, limit kredit, dan jatuh tempo.',
        href: '/master-data/customers',
        icon: UserRound,
    },
    {
        title: 'Supplier',
        description: 'Supplier, rekening, salesman, dan barang yang dipasok.',
        href: '/master-data/suppliers',
        icon: Truck,
    },
    {
        title: 'Toko & Gudang',
        description: 'Node dan lokasi stok di setiap node.',
        href: '/master-data/nodes',
        icon: Store,
    },
];

export default function MasterDataIndex() {
    return (
        <>
            <Head title="Master Data" />
            <div className="flex flex-col gap-4 p-4">
                <PageHeader
                    title="Master Data"
                    description="Data induk yang dikirim ke semua toko dan gudang."
                />
                <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                    {sections.map((s) => (
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
            </div>
        </>
    );
}

MasterDataIndex.layout = {
    breadcrumbs: [{ title: 'Master Data', href: '/master-data' }],
};
