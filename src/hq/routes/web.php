<?php

use App\Http\Controllers\Auth\LoginController;
use App\Http\Controllers\MasterData\BrandController;
use App\Http\Controllers\MasterData\CategoryController;
use App\Http\Controllers\MasterData\ProductController;
use App\Http\Controllers\MasterData\ProductUnitController;
use App\Http\Controllers\MasterData\UnitController;
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

    // Master data: reading needs master.view (or master.edit), every change master.edit.
    Route::prefix('master-data')->middleware('permission:master.view,master.edit')->group(function () {
        Route::inertia('/', 'master-data/index')->name('master-data');
        Route::get('units', [UnitController::class, 'index'])->name('master-data.units');
        Route::get('categories', [CategoryController::class, 'index'])->name('master-data.categories');
        Route::get('brands', [BrandController::class, 'index'])->name('master-data.brands');
        Route::get('products', [ProductController::class, 'index'])->name('master-data.products');
        Route::get('products/{product}/edit', [ProductController::class, 'edit'])->name('master-data.products.edit');

        Route::middleware('permission:master.edit')->group(function () {
            Route::post('units', [UnitController::class, 'store']);
            Route::put('units/{id}', [UnitController::class, 'update']);
            Route::delete('units/{id}', [UnitController::class, 'destroy']);
            Route::post('categories', [CategoryController::class, 'store']);
            Route::put('categories/{id}', [CategoryController::class, 'update']);
            Route::delete('categories/{id}', [CategoryController::class, 'destroy']);
            Route::post('brands', [BrandController::class, 'store']);
            Route::put('brands/{id}', [BrandController::class, 'update']);
            Route::delete('brands/{id}', [BrandController::class, 'destroy']);

            Route::get('products/create', [ProductController::class, 'create'])->name('master-data.products.create');
            Route::post('products', [ProductController::class, 'store']);
            Route::put('products/{id}', [ProductController::class, 'update']);
            Route::delete('products/{id}', [ProductController::class, 'destroy']);
            Route::post('products/{product}/units', [ProductUnitController::class, 'store']);
            Route::put('products/{product}/units/{unit}', [ProductUnitController::class, 'update']);
            Route::delete('products/{product}/units/{unit}', [ProductUnitController::class, 'destroy']);
            Route::post('products/{product}/units/{unit}/barcodes', [ProductUnitController::class, 'storeBarcode']);
            Route::delete('products/{product}/barcodes/{barcode}', [ProductUnitController::class, 'destroyBarcode']);
            Route::post('products/{product}/units/{unit}/prices', [ProductUnitController::class, 'storePrice']);
            Route::delete('products/{product}/prices/{price}', [ProductUnitController::class, 'destroyPrice']);
        });
    });

    // Menu pages; their content comes with later tasks.
    foreach ([
        'pembelian' => 'Pembelian',
        'stok' => 'Stok',
        'laporan' => 'Laporan',
        'pengaturan' => 'Pengaturan',
    ] as $path => $title) {
        Route::inertia($path, 'placeholder', ['title' => $title, 'href' => "/{$path}"])->name($path);
    }
});
