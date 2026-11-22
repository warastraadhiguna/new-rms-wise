<?php

use Illuminate\Support\Facades\Route;

// 11a: skeleton only. Login (POST), permissions and the menu pages come with 11b.
Route::redirect('/', '/dashboard')->name('home');

Route::middleware('guest')->group(function () {
    Route::inertia('login', 'auth/login')->name('login');
});

Route::middleware('auth')->group(function () {
    Route::inertia('dashboard', 'dashboard')->name('dashboard');
});
