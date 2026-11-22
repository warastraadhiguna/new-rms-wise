<?php

use App\Http\Controllers\Api\SyncController;
use Illuminate\Support\Facades\Route;

// Sync API for the nodes (docs/sync-contract.md): bearer token per node, gzip, rate limited per node.
Route::prefix('v1/sync')->middleware(['sync.compression', 'sync.node', 'throttle:sync'])->group(function () {
    Route::get('pull', [SyncController::class, 'pull']);
    Route::post('push', [SyncController::class, 'push']);
    Route::post('heartbeat', [SyncController::class, 'heartbeat']);
});
