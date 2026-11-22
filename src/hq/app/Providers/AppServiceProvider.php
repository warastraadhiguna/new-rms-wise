<?php

namespace App\Providers;

use App\Models\User;
use Carbon\CarbonImmutable;
use Illuminate\Cache\RateLimiting\Limit;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\Date;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Gate;
use Illuminate\Support\Facades\RateLimiter;
use Illuminate\Support\ServiceProvider;
use Illuminate\Validation\Rules\Password;

class AppServiceProvider extends ServiceProvider
{
    /**
     * Register any application services.
     */
    public function register(): void
    {
        //
    }

    /**
     * Bootstrap any application services.
     */
    public function boot(): void
    {
        $this->configureDefaults();

        // Every ability is a permission from the user's roles ("*" = all): can:master.edit, Gate::allows(...), @can.
        Gate::before(fn (mixed $user, string $ability) => $user instanceof User && $user->hasPermission($ability) ? true : null);

        // Sync API: limited per node token (one active token per node), so one busy node never slows the others down.
        // Keyed on the token itself: Laravel runs throttling before the node middleware.
        RateLimiter::for('sync', function (Request $request) {
            $token = (string) $request->bearerToken();

            return Limit::perMinute((int) config('pos.sync.rate_per_minute'))
                ->by($token === '' ? 'ip:'.$request->ip() : 'token:'.hash('sha256', $token))
                ->response(fn () => response()->json(['code' => 'rate_limited', 'message' => 'Terlalu banyak request; coba lagi sebentar.'], 429));
        });
    }

    /**
     * Configure default behaviors for production-ready applications.
     */
    protected function configureDefaults(): void
    {
        Date::use(CarbonImmutable::class);

        DB::prohibitDestructiveCommands(
            app()->isProduction(),
        );

        Password::defaults(fn (): ?Password => app()->isProduction()
            ? Password::min(12)
                ->mixedCase()
                ->letters()
                ->numbers()
                ->symbols()
                ->uncompromised()
            : null,
        );
    }
}
