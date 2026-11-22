<?php

use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Support\Facades\DB;
use Inertia\Testing\AssertableInertia as Assert;

/*
| Tugas 13c: the HQ dashboard — every node's sync state (seen, outbox, master lag, clock, version, colour by
| sync.alert_minutes) and today's sales per store from the pushed copies.
*/

function heartbeat(object $node, ?int $minutesAgo, array $values = []): void
{
    DB::table('node_heartbeats')->where('node_id', $node->id)->delete();
    if ($minutesAgo !== null) {
        DB::table('node_heartbeats')->insert(array_merge([
            'node_id' => $node->id, 'last_seen_at' => now('UTC')->subMinutes($minutesAgo)->format('Y-m-d H:i:s.v'),
            'pending_outbox' => 0, 'failed_outbox' => 0, 'master_version' => 0, 'clock_skew_ms' => 0, 'app_version' => '1.0.0',
        ], $values));
    }
}

/** @return array<string, mixed> the node's row on the dashboard */
function dashboardNode(object $node): array
{
    $found = null;
    test()->get('/dashboard')->assertOk()->assertInertia(function (Assert $page) use ($node, &$found) {
        $page->component('dashboard');
        $found = collect($page->toArray()['props']['nodes'])->firstWhere('id', $node->id);
    });

    return (array) $found;
}

it('shows each node with last contact, outbox, master lag, clock and version', function () {
    $this->actingAs(editor(['hq.access']));
    $node = syncNode();
    $master = (int) DB::table('master_version')->value('last_version');
    heartbeat($node, 2, ['pending_outbox' => 7, 'failed_outbox' => 0, 'master_version' => $master - 3, 'clock_skew_ms' => 4200, 'app_version' => '1.2.0']);

    $row = dashboardNode($node);
    expect($row)->toMatchArray([
        'status' => 'green', 'minutes_since_seen' => 2, 'pending_outbox' => 7, 'master_lag' => 3,
        'clock_skew_seconds' => 4, 'app_version' => '1.2.0', 'alert_minutes' => 15,
    ]);
});

it('colours nodes by sync.alert_minutes of their branch, else global, else 15', function () {
    $this->actingAs(editor(['hq.access']));
    $node = syncNode();

    heartbeat($node, null);
    expect(dashboardNode($node))->toMatchArray(['status' => 'red', 'last_seen_at' => null, 'pending_outbox' => null]);

    heartbeat($node, 20);
    expect(dashboardNode($node)['status'])->toBe('yellow');                  // > 15, <= 30
    heartbeat($node, 40);
    expect(dashboardNode($node)['status'])->toBe('red');

    app(MasterDataWriter::class)->create('settings', ['branch_id' => $node->branch, 'setting_key' => 'sync.alert_minutes', 'setting_value' => '60'], WriteContext::system());
    $row = dashboardNode($node);
    expect($row['status'])->toBe('green')->and($row['alert_minutes'])->toBe(60);

    heartbeat($node, 1, ['failed_outbox' => 2]);
    $row = dashboardNode($node);
    expect($row['status'])->toBe('yellow')->and($row['reasons'][0])->toContain('2 dokumen ditolak');
});

it('marks a node seen through the sync API and lists stores and warehouses only', function () {
    $this->actingAs(editor(['hq.access']));
    $node = syncNode();
    heartbeat($node, null);
    $this->withToken($node->token)->postJson('/api/v1/sync/heartbeat', [
        'master_version' => 1, 'pending_outbox' => 0, 'node_time' => now('UTC')->format('Y-m-d H:i:s.v'),
    ])->assertOk();

    $this->actingAs(editor(['hq.access']));
    expect(dashboardNode($node)['status'])->toBe('green');

    $this->get('/dashboard')->assertInertia(fn (Assert $page) => $page
        ->where('nodes', fn ($nodes) => collect($nodes)->every(fn ($n) => $n['type'] !== 'HQ')));
});

it('sums today\'s sales per store from the pushed copies, without voided sales and other days', function () {
    $node = syncNode();
    $cashier = makeUser()->id;
    $product = syncProduct();
    $shift = id7();
    $now = now('UTC');
    $today = $now->format('Y-m-d H:i:s.v');
    $yesterday = $now->setTimezone('Asia/Jakarta')->startOfDay()->subHours(2)->utc()->format('Y-m-d H:i:s.v');
    [$a, $b, $c, $old] = [id7(), id7(), id7(), id7()];

    push($node, [
        shiftDoc($node, $shift, id7(), $cashier),
        saleDoc($node, $a, $shift, $cashier, $product, 2, '10000.0000', $today),
        saleDoc($node, $b, $shift, $cashier, $product, 3, '2500.5000', $today),
        saleDoc($node, $c, $shift, $cashier, $product, 4, '9999.0000', $today),
        saleDoc($node, $old, $shift, $cashier, $product, 5, '50000.0000', $yesterday),
        voidDoc($node, $c, $cashier, 6),
    ])->assertOk();

    $this->actingAs(editor(['hq.access']));
    $this->get('/dashboard')->assertOk()->assertInertia(function (Assert $page) use ($node) {
        $row = collect($page->toArray()['props']['salesToday'])->firstWhere('node_id', $node->id);
        expect($row)->toMatchArray([
            'sale_count' => 2, 'sales_total' => '12500.5000', 'return_total' => '0.0000', 'net_total' => '12500.5000',
            'date' => now('Asia/Jakarta')->format('Y-m-d'),
        ]);
    });
});

it('needs a login with hq.access', function () {
    $this->get('/dashboard')->assertRedirect('/login');
    $this->actingAs(editor(['master.view']));
    $this->get('/dashboard')->assertRedirect('/login');                  // no hq.access: logged out (EnsureHqAccess)
});
