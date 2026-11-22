import { Link } from '@inertiajs/react';
import {
    BarChart3,
    Boxes,
    Database,
    LayoutGrid,
    Settings,
    ShoppingCart,
} from 'lucide-react';
import AppLogo from '@/components/app-logo';
import { NavMain } from '@/components/nav-main';
import {
    Sidebar,
    SidebarContent,
    SidebarHeader,
    SidebarMenu,
    SidebarMenuButton,
    SidebarMenuItem,
} from '@/components/ui/sidebar';
import type { NavItem } from '@/types';

/** Menu of the HQ web (pages are placeholders until their tasks). */
export const mainNavItems: NavItem[] = [
    { title: 'Dashboard', href: '/dashboard', icon: LayoutGrid },
    { title: 'Master Data', href: '/master-data', icon: Database },
    { title: 'Pembelian', href: '/pembelian', icon: ShoppingCart },
    { title: 'Stok', href: '/stok', icon: Boxes },
    { title: 'Laporan', href: '/laporan', icon: BarChart3 },
    { title: 'Pengaturan', href: '/pengaturan', icon: Settings },
];

export function AppSidebar() {
    return (
        <Sidebar collapsible="icon" variant="inset">
            <SidebarHeader>
                <SidebarMenu>
                    <SidebarMenuItem>
                        <SidebarMenuButton size="lg" asChild>
                            <Link href="/dashboard" prefetch>
                                <AppLogo />
                            </Link>
                        </SidebarMenuButton>
                    </SidebarMenuItem>
                </SidebarMenu>
            </SidebarHeader>

            <SidebarContent>
                <NavMain items={mainNavItems} />
            </SidebarContent>
        </Sidebar>
    );
}
