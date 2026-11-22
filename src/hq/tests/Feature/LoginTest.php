<?php

use App\Http\Controllers\Auth\LoginController;
use App\Models\User;
use Illuminate\Support\Facades\DB;
use Inertia\Testing\AssertableInertia as Assert;

function login(string $username, string $password)
{
    return test()->from('/login')->post('/login', ['username' => $username, 'password' => $password]);
}

const LOCKED_MESSAGE = 'Terlalu banyak percobaan gagal. Coba lagi dalam 5 menit.';

it('logs in with username and password and shows the layout with the user', function () {
    $user = makeUser();

    login($user->username, $user->password)->assertRedirect('/dashboard');

    $this->assertAuthenticatedAs(User::find($user->id));
    $this->get('/dashboard')->assertOk()->assertInertia(fn (Assert $page) => $page
        ->component('dashboard')
        ->where('auth.user.username', $user->username)
        ->where('auth.user.full_name', 'Pengguna '.$user->username)
        ->missing('auth.user.password_hash')
        ->where('auth.permissions', ['hq.access']));
});

it('accepts the username in any letter case and with spaces around it', function () {
    $user = makeUser();

    login('  '.strtoupper($user->username).' ', $user->password)->assertRedirect('/dashboard');
});

it('gives the same answer for a wrong password, an unknown, an inactive and a deleted user', function () {
    $active = makeUser();
    $inactive = makeUser(active: false);
    $deleted = makeUser(deleted: true);

    foreach ([
        [$active->username, 'salah-sekali'],
        ['tidak-ada-'.uniqid(), 'apa-saja'],
        [$inactive->username, $inactive->password],
        [$deleted->username, $deleted->password],
    ] as [$username, $password]) {
        login($username, $password)
            ->assertRedirect('/login')
            ->assertSessionHasErrors(['username' => LoginController::INVALID_MESSAGE]);
        $this->assertGuest();
    }
});

it('refuses a correct password without hq.access', function () {
    $cashier = makeUser(['sale.create', 'shift.open']);

    login($cashier->username, $cashier->password)
        ->assertSessionHasErrors(['username' => LoginController::NO_ACCESS_MESSAGE]);
    $this->assertGuest();
});

it('accepts "*" as hq.access', function () {
    $owner = makeUser(['*']);

    login($owner->username, $owner->password)->assertRedirect('/dashboard');
});

it('locks a login name for 5 minutes after 5 failures, also for the right password and for unknown names', function () {
    $user = makeUser();
    foreach (range(1, 5) as $_) {
        login($user->username, 'salah')->assertSessionHasErrors(['username' => LoginController::INVALID_MESSAGE]);
    }

    login($user->username, $user->password)->assertSessionHasErrors(['username' => LOCKED_MESSAGE]);
    $this->assertGuest();

    // an unknown name locks the same way, so a lock says nothing about whether the account exists
    $unknown = 'tidak-ada-'.uniqid();
    foreach (range(1, 5) as $_) {
        login($unknown, 'x');
    }
    login($unknown, 'x')->assertSessionHasErrors(['username' => LOCKED_MESSAGE]);

    // after the lock the right password works again and the count is reset
    DB::table('login_throttle')->where('login_name', $user->username)->update(['locked_until' => now('UTC')->subSecond()]);
    login($user->username, $user->password)->assertRedirect('/dashboard');
    expect(DB::table('login_throttle')->where('login_name', $user->username)->value('failed_count'))->toBe(0);
});

it('starts counting again after a successful login', function () {
    $user = makeUser();
    foreach (range(1, 4) as $_) {
        login($user->username, 'salah');
    }
    login($user->username, $user->password)->assertRedirect('/dashboard');
    $this->post('/logout');

    foreach (range(1, 4) as $_) {
        login($user->username, 'salah');
    }
    login($user->username, $user->password)->assertRedirect('/dashboard');
});

it('ends the session when the account is deactivated, deleted or loses hq.access', function (string $change) {
    $user = makeUser();
    login($user->username, $user->password)->assertRedirect('/dashboard');

    match ($change) {
        'inactive' => DB::table('users')->where('id', $user->id)->update(['is_active' => false]),
        'deleted' => DB::table('users')->where('id', $user->id)->update(['deleted_at' => now('UTC')]),
        'no access' => DB::table('user_roles')->where('user_id', $user->id)->update(['deleted_at' => now('UTC')]),
    };

    $this->get('/dashboard')->assertRedirect('/login');
    $this->assertGuest();
})->with(['inactive', 'deleted', 'no access']);

it('logs out', function () {
    $user = makeUser();
    login($user->username, $user->password);

    $this->post('/logout')->assertRedirect('/login');

    $this->assertGuest();
    $this->get('/dashboard')->assertRedirect('/login');
});

it('shows every menu page to a logged-in user', function () {
    $user = makeUser();
    login($user->username, $user->password);

    foreach (['pembelian' => 'Pembelian', 'stok' => 'Stok', 'laporan' => 'Laporan'] as $path => $title) {
        $this->get("/{$path}")->assertOk()->assertInertia(fn (Assert $page) => $page->component('placeholder')->where('title', $title));
    }
    $this->get('/pengaturan')->assertOk()->assertInertia(fn (Assert $page) => $page->component('settings/index'));
});

it('requires both fields', function () {
    $this->from('/login')->post('/login', [])
        ->assertSessionHasErrors(['username' => 'Isi username.', 'password' => 'Isi password.']);
});

it('sends a logged-in user from the login page to the dashboard', function () {
    $user = makeUser();
    login($user->username, $user->password);

    $this->get('/login')->assertRedirect('/dashboard');
});
