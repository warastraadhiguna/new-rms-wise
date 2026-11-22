<?php

namespace App\Http\Controllers\Auth;

use App\Auth\HqLogin;
use App\Auth\LoginOutcome;
use App\Http\Controllers\Controller;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\Auth;
use Illuminate\Validation\ValidationException;
use Inertia\Inertia;
use Inertia\Response;

/** Login with username + password; no registration, no "remember me", no password reset by email. */
class LoginController extends Controller
{
    /** The same answer for an unknown user, a wrong password, an inactive or a deleted account. */
    public const INVALID_MESSAGE = 'Username atau password salah.';

    public const NO_ACCESS_MESSAGE = 'Akun ini tidak memiliki akses ke web pusat.';

    public function create(): Response
    {
        return Inertia::render('auth/login');
    }

    public function store(Request $request, HqLogin $login): RedirectResponse
    {
        $credentials = $request->validate([
            'username' => ['required', 'string', 'max:50'],
            'password' => ['required', 'string', 'max:200'],
        ], [
            'username.required' => 'Isi username.',
            'password.required' => 'Isi password.',
        ]);

        $outcome = $login->attempt($credentials['username'], $credentials['password']);

        if ($outcome->status !== LoginOutcome::SUCCESS || $outcome->user === null) {
            throw ValidationException::withMessages(['username' => match ($outcome->status) {
                LoginOutcome::LOCKED => 'Terlalu banyak percobaan gagal. Coba lagi dalam '
                    .max(1, (int) ceil($outcome->lockedSeconds / 60)).' menit.',
                LoginOutcome::NO_ACCESS => self::NO_ACCESS_MESSAGE,
                default => self::INVALID_MESSAGE,
            }]);
        }

        Auth::login($outcome->user);
        $request->session()->regenerate();

        return redirect()->intended('/dashboard');
    }

    public function destroy(Request $request): RedirectResponse
    {
        Auth::guard('web')->logout();
        $request->session()->invalidate();
        $request->session()->regenerateToken();

        return redirect('/login');
    }
}
