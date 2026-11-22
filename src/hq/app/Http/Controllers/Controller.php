<?php

namespace App\Http\Controllers;

use Illuminate\Http\RedirectResponse;
use Inertia\Inertia;

abstract class Controller
{
    /** Back (or to $to) with a success toast (Indonesian message) shown by the layout. */
    protected function saved(string $message, ?string $to = null): RedirectResponse
    {
        Inertia::flash('toast', ['type' => 'success', 'message' => $message]);

        return $to === null ? back() : redirect($to);
    }
}
