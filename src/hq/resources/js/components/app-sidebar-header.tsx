import { Link, router, usePage } from '@inertiajs/react';
import { LogOut } from 'lucide-react';
import { Breadcrumbs } from '@/components/breadcrumbs';
import { Button } from '@/components/ui/button';
import { SidebarTrigger } from '@/components/ui/sidebar';
import { UserInfo } from '@/components/user-info';
import type { BreadcrumbItem as BreadcrumbItemType } from '@/types';

/** Header: breadcrumbs on the left, the user's name and "Keluar" on the right. */
export function AppSidebarHeader({
    breadcrumbs = [],
}: {
    breadcrumbs?: BreadcrumbItemType[];
}) {
    const { auth } = usePage().props;

    return (
        <header className="flex h-16 shrink-0 items-center gap-2 border-b border-sidebar-border/50 px-6 transition-[width,height] ease-linear group-has-data-[collapsible=icon]/sidebar-wrapper:h-12 md:px-4">
            <div className="flex items-center gap-2">
                <SidebarTrigger className="-ml-1" />
                <Breadcrumbs breadcrumbs={breadcrumbs} />
            </div>
            {auth.user && (
                <div className="ml-auto flex items-center gap-3">
                    <div className="hidden items-center gap-2 sm:flex">
                        <UserInfo user={auth.user} />
                    </div>
                    <Button variant="outline" size="sm" asChild>
                        <Link
                            href="/logout"
                            method="post"
                            as="button"
                            onClick={() => router.flushAll()}
                            data-test="logout-button"
                        >
                            <LogOut className="mr-1 size-4" />
                            Keluar
                        </Link>
                    </Button>
                </div>
            )}
        </header>
    );
}
