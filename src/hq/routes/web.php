<?php

use App\Http\Controllers\Auth\LoginController;
use App\Http\Controllers\DashboardController;
use App\Http\Controllers\MasterData\BrandController;
use App\Http\Controllers\MasterData\CategoryController;
use App\Http\Controllers\MasterData\ContactController;
use App\Http\Controllers\MasterData\NodeController;
use App\Http\Controllers\MasterData\ProductController;
use App\Http\Controllers\MasterData\ProductUnitController;
use App\Http\Controllers\MasterData\SupplierController;
use App\Http\Controllers\MasterData\UnitController;
use App\Http\Controllers\Settings\ExpenseTypeController;
use App\Http\Controllers\Settings\PaymentMethodController;
use App\Http\Controllers\Settings\ReasonController;
use App\Http\Controllers\Settings\RoleController;
use App\Http\Controllers\Settings\SettingController;
use App\Http\Controllers\Settings\UserController;
use Illuminate\Support\Facades\Route;

Route::redirect('/', '/dashboard')->name('home');

Route::middleware('guest')->group(function () {
    Route::get('login', [LoginController::class, 'create'])->name('login');
    Route::post('login', [LoginController::class, 'store'])->name('login.store');
});

Route::post('logout', [LoginController::class, 'destroy'])->middleware('auth')->name('logout');

// The HQ web: logged in, active, with permission hq.access (re-checked on every request).
Route::middleware(['auth', 'hq'])->group(function () {
    Route::get('dashboard', DashboardController::class)->name('dashboard');

    // Master data: reading needs master.view (or master.edit), every change master.edit.
    Route::prefix('master-data')->middleware('permission:master.view,master.edit')->group(function () {
        Route::inertia('/', 'master-data/index')->name('master-data');
        Route::get('units', [UnitController::class, 'index'])->name('master-data.units');
        Route::get('categories', [CategoryController::class, 'index'])->name('master-data.categories');
        Route::get('brands', [BrandController::class, 'index'])->name('master-data.brands');
        Route::get('products', [ProductController::class, 'index'])->name('master-data.products');
        Route::get('products/{product}/edit', [ProductController::class, 'edit'])->name('master-data.products.edit');
        Route::get('{kind}', [ContactController::class, 'index'])->whereIn('kind', array_keys(ContactController::KINDS))->name('master-data.contacts');
        Route::get('suppliers/{id}', [SupplierController::class, 'show'])->name('master-data.suppliers.show');
        Route::get('nodes', [NodeController::class, 'index'])->name('master-data.nodes');
        Route::get('nodes/{id}', [NodeController::class, 'show'])->name('master-data.nodes.show');

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
            Route::post('products/{id}/deactivate', [ProductController::class, 'deactivate']);
            Route::post('products/{product}/units', [ProductUnitController::class, 'store']);
            Route::put('products/{product}/units/{unit}', [ProductUnitController::class, 'update']);
            Route::delete('products/{product}/units/{unit}', [ProductUnitController::class, 'destroy']);
            Route::post('products/{product}/units/{unit}/barcodes', [ProductUnitController::class, 'storeBarcode']);
            Route::delete('products/{product}/barcodes/{barcode}', [ProductUnitController::class, 'destroyBarcode']);
            Route::post('products/{product}/units/{unit}/prices', [ProductUnitController::class, 'storePrice']);
            Route::delete('products/{product}/prices/{price}', [ProductUnitController::class, 'destroyPrice']);

            Route::post('{kind}', [ContactController::class, 'store'])->whereIn('kind', array_keys(ContactController::KINDS));
            Route::put('{kind}/{id}', [ContactController::class, 'update'])->whereIn('kind', array_keys(ContactController::KINDS));
            Route::post('suppliers/{id}/reps', [SupplierController::class, 'storeRep']);
            Route::put('suppliers/{id}/reps/{rep}', [SupplierController::class, 'updateRep']);
            Route::delete('suppliers/{id}/reps/{rep}', [SupplierController::class, 'destroyRep']);
            Route::post('suppliers/{id}/products', [SupplierController::class, 'storeProduct']);
            Route::delete('suppliers/{id}/products/{item}', [SupplierController::class, 'destroyProduct']);

            Route::post('nodes', [NodeController::class, 'store']);
            Route::put('nodes/{id}', [NodeController::class, 'update']);
            Route::post('nodes/{id}/locations', [NodeController::class, 'storeLocation']);
            Route::put('nodes/{id}/locations/{location}', [NodeController::class, 'updateLocation']);
            Route::post('nodes/{id}/sync-token', [NodeController::class, 'createToken']);
            Route::delete('nodes/{id}/sync-token', [NodeController::class, 'revokeToken']);
        });
    });

    // Pengaturan: the hub shows only what the user may open; every page checks its own permission.
    Route::prefix('pengaturan')->group(function () {
        Route::inertia('/', 'settings/index')->name('pengaturan');

        Route::middleware('permission:users.manage')->group(function () {
            Route::get('users', [UserController::class, 'index'])->name('pengaturan.users');
            Route::post('users', [UserController::class, 'store']);
            Route::put('users/{id}', [UserController::class, 'update']);
            Route::put('users/{id}/password', [UserController::class, 'password']);
            Route::post('users/{id}/roles', [UserController::class, 'assignRole']);
            Route::delete('users/{id}/roles/{assignment}', [UserController::class, 'removeRole']);

            Route::get('roles', [RoleController::class, 'index'])->name('pengaturan.roles');
            Route::post('roles', [RoleController::class, 'store']);
            Route::put('roles/{id}', [RoleController::class, 'update']);
            Route::delete('roles/{id}', [RoleController::class, 'destroy']);
        });

        Route::middleware('permission:settings.manage')->group(function () {
            Route::get('settings', [SettingController::class, 'index'])->name('pengaturan.settings');
            Route::put('settings', [SettingController::class, 'update']);
            Route::get('settings/receipt-preview', [SettingController::class, 'receiptPreview']);

            Route::get('payment-methods', [PaymentMethodController::class, 'index'])->name('pengaturan.payment-methods');
            Route::post('payment-methods', [PaymentMethodController::class, 'store']);
            Route::post('payment-methods/return', [PaymentMethodController::class, 'storeReturn']);
            Route::put('payment-methods/{id}', [PaymentMethodController::class, 'update']);

            Route::get('reasons', [ReasonController::class, 'index'])->name('pengaturan.reasons');
            Route::post('reasons', [ReasonController::class, 'store']);
            Route::put('reasons/{id}', [ReasonController::class, 'update']);
            Route::delete('reasons/{id}', [ReasonController::class, 'destroy']);

            Route::get('expense-types', [ExpenseTypeController::class, 'index'])->name('pengaturan.expense-types');
            Route::post('expense-types', [ExpenseTypeController::class, 'store']);
            Route::put('expense-types/{id}', [ExpenseTypeController::class, 'update']);
            Route::delete('expense-types/{id}', [ExpenseTypeController::class, 'destroy']);
        });
    });

    // Menu pages; their content comes with later tasks.
    foreach ([
        'pembelian' => 'Pembelian',
        'stok' => 'Stok',
        'laporan' => 'Laporan',
    ] as $path => $title) {
        Route::inertia($path, 'placeholder', ['title' => $title, 'href' => "/{$path}"])->name($path);
    }
});
