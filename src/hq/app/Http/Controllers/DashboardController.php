<?php

namespace App\Http\Controllers;

use App\Support\Monitoring\NodeMonitor;
use Carbon\CarbonImmutable;
use Inertia\Inertia;
use Inertia\Response;

/** Dashboard: every node's sync state (green / yellow / red) and today's sales per store as far as they reached HQ. */
class DashboardController extends Controller
{
    public function __invoke(NodeMonitor $monitor): Response
    {
        $now = CarbonImmutable::now('UTC');

        return Inertia::render('dashboard', [
            'nodes' => $monitor->nodes($now),
            'salesToday' => $monitor->salesToday($now),
            'checkedAt' => $now->setTimezone((string) config('pos.display_timezone'))->format('H:i'),
        ]);
    }
}
