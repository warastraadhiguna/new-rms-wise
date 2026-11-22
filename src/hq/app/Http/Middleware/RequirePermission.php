<?php

namespace App\Http\Middleware;

use App\Models\User;
use Closure;
use Illuminate\Http\Request;
use Symfony\Component\HttpFoundation\Response;

/**
 * Route middleware `permission:name[,name2]`: the user needs at least one of the permissions ("*" grants all).
 * The same check is available as a gate: `can:name`, `Gate::allows('name')`, `$user->can('name')`.
 */
class RequirePermission
{
    public function handle(Request $request, Closure $next, string ...$permissions): Response
    {
        $user = $request->user();

        foreach ($permissions as $permission) {
            if ($user instanceof User && $user->hasPermission($permission)) {
                return $next($request);
            }
        }

        abort(403, 'Anda tidak memiliki izin untuk halaman ini.');
    }
}
