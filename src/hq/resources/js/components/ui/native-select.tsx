import * as React from 'react';
import { cn } from '@/lib/utils';

/**
 * A plain <select> styled like Input. Used where an empty choice ("Semua", "Tanpa kategori") is needed and for
 * dense forms; posts its value with the form like any input.
 */
function NativeSelect({ className, ...props }: React.ComponentProps<'select'>) {
    return (
        <select
            data-slot="native-select"
            className={cn(
                'flex h-9 w-full min-w-0 rounded-md border border-input bg-transparent px-2 py-1 text-sm shadow-xs outline-none',
                'focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50',
                'aria-invalid:border-destructive dark:bg-input/30',
                className,
            )}
            {...props}
        />
    );
}

export { NativeSelect };
