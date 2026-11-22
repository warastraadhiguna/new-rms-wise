<?php

use App\Http\Controllers\Auth\LoginController;
use Illuminate\Support\Facades\Route;

Route::redirect('/', '/dashboard')->name('home');

Route::middleware('guest')->group(function () {
    Route::get('login', [LoginController::class, 'create'])->name('login');
    Route::post('login', [LoginController::class, 'store'])->name('login.store');
});

Route::post('logout', [LoginController::class, 'destroy'])->middleware('auth')->name('logout');

// The HQ web: logged in, active, with permission hq.access (re-checked on every request).
Route::middleware(['auth', 'hq'])->group(function () {
    Route::inertia('dashboard', 'dashboard')->name('dashboard');

    // Menu pages; their content comes with later tasks.
    foreach ([
        'master-data' => 'Master Data',
        'pembelian' => 'Pembelian',
        'stok' => 'Stok',
        'laporan' => 'Laporan',
        'pengaturan' => 'Pengaturan',
    ] as $path => $title) {
        Route::inertia($path, 'placeholder', ['title' => $title, 'href' => "/{$path}"])->name($path);
    }
});
