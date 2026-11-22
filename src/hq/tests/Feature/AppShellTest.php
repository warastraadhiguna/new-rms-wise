<?php

use Inertia\Testing\AssertableInertia as Assert;

it('shows the login page and no registration or password reset', function () {
    $this->get('/login')
        ->assertOk()
        ->assertInertia(fn (Assert $page) => $page->component('auth/login'));

    $this->get('/register')->assertNotFound();
    $this->get('/forgot-password')->assertNotFound();
    $this->get('/email/verify')->assertNotFound();
});

it('sends guests to the login page', function () {
    $this->get('/')->assertRedirect('/dashboard');
    $this->get('/dashboard')->assertRedirect('/login');
});

it('runs in UTC', function () {
    expect(config('app.timezone'))->toBe('UTC')
        ->and(date_default_timezone_get())->toBe('UTC')
        ->and(DB::selectOne('SELECT @@session.time_zone AS tz')->tz)->toBe('+00:00');
});
