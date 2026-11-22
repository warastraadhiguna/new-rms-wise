<?php

namespace App\Http\Middleware;

use App\Auth\UserPermissions;
use App\Http\Controllers\Auth\LoginController;
use App\Models\User;
use Closure;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\Auth;
use Symfony\Component\HttpFoundation\Response;

/**
 * Every request of the HQ web re-checks the account (the user row is read again per request): still active, not
 * deleted, and still holding hq.access. Otherwise the session ends and the user is sent to the login page.
 */
class EnsureHqAccess
{
    public function handle(Request $request, Closure $next): Response
    {
        // Read again: an account deactivated or deleted a second ago must not keep a session (nor a cached instance).
        $current = $request->user();
        $user = $current instanceof User ? User::query()->find($current->getAuthIdentifier()) : null;
        if ($user instanceof User) {
            Auth::guard('web')->setUser($user);
        }

        if (! $user instanceof User || ! $user->isActiveAccount() || ! $user->hasPermission(UserPermissions::HQ_ACCESS)) {
            Auth::guard('web')->logout();
            $request->session()->invalidate();
            $request->session()->regenerateToken();

            return redirect('/login')->withErrors(['username' => $user instanceof User && $user->isActiveAccount()
                ? LoginController::NO_ACCESS_MESSAGE
                : 'Sesi berakhir. Silakan masuk kembali.']);
        }

        return $next($request);
    }
}
