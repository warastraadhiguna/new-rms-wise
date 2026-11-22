<?php

use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use App\Sync\NodeTokens;
use Illuminate\Support\Facades\DB;
use Inertia\Testing\AssertableInertia as Assert;

/*
| Tugas 13a: the sync API (docs/sync-contract.md) — node tokens, pull with a safe cursor, push with ownership checks and
| idempotency, heartbeat, limits and gzip.
*/

// ---- tokens ------------------------------------------------------------------------------------------

it('refuses requests without a valid, active token and for inactive nodes', function () {
    $this->getJson('/api/v1/sync/pull')->assertStatus(401)->assertJson(['code' => 'invalid_token']);
    $this->withToken('pos_salah')->getJson('/api/v1/sync/pull')->assertStatus(401);

    $node = syncNode();
    $old = $node->token;
    $node->token = app(NodeTokens::class)->create($node->id, null);          // a new token revokes the old one
    $this->withToken($old)->getJson('/api/v1/sync/pull')->assertStatus(401);
    $this->withToken($node->token)->getJson('/api/v1/sync/pull')->assertOk();

    app(NodeTokens::class)->revoke($node->id, null);
    $this->withToken($node->token)->getJson('/api/v1/sync/pull')->assertStatus(401);

    $inactive = syncNode(active: false);
    $this->withToken($inactive->token)->getJson('/api/v1/sync/pull')->assertStatus(403)->assertJson(['code' => 'node_inactive']);
});

it('creates a token on the node page, shows it once, and revokes it', function () {
    $this->actingAs(editor());
    $node = syncNode();
    $old = $node->token;

    $response = $this->post("/master-data/nodes/{$node->id}/sync-token")->assertSessionHasNoErrors();
    $token = (string) session('inertia.flash_data.sync_token', '');
    if ($token === '') {
        $this->get("/master-data/nodes/{$node->id}")->assertInertia(function (Assert $page) use (&$token) {
            $token = (string) ($page->toArray()['flash']['sync_token'] ?? '');
        });
    }
    expect($token)->toStartWith('pos_')
        ->and(DB::table('node_sync_tokens')->where('token_hash', hash('sha256', $token))->whereNull('revoked_at')->exists())->toBeTrue()
        ->and(DB::table('node_sync_tokens')->where('token_hash', hash('sha256', $old))->value('revoked_at'))->not->toBeNull()
        ->and(DB::table('node_sync_tokens')->where('node_id', $node->id)->pluck('token_hash')->all())->not->toContain($token);

    $this->get("/master-data/nodes/{$node->id}")->assertInertia(fn (Assert $page) => $page->where('syncToken.token_hint', '…'.substr($token, -6)));

    $this->delete("/master-data/nodes/{$node->id}/sync-token")->assertSessionHasNoErrors();
    $this->withToken($token)->getJson('/api/v1/sync/pull')->assertStatus(401);
    $response->assertRedirect();
});

// ---- pull --------------------------------------------------------------------------------------------

it('pulls master data after a version in order, with deleted rows, filtered by branch, page by page', function () {
    $node = syncNode();
    $other = newBranch();
    $writer = app(MasterDataWriter::class);
    $ctx = WriteContext::system();
    $since = (int) DB::table('master_version')->value('last_version');

    $unit = $writer->create('units', ['code' => uid('U'), 'name' => 'Pull 1'], $ctx);
    $brand = $writer->create('brands', ['name' => 'Merek '.uid()], $ctx);
    $writer->delete('brands', $brand, $ctx);
    $own = $writer->create('settings', ['branch_id' => $node->branch, 'setting_key' => 'returns.window_days', 'setting_value' => '3'], $ctx);
    $foreign = $writer->create('settings', ['branch_id' => $other, 'setting_key' => 'returns.window_days', 'setting_value' => '9'], $ctx);

    $all = $this->withToken($node->token)->getJson("/api/v1/sync/pull?since={$since}&limit=1000")->assertOk()->json();
    $ids = array_map(fn ($c) => $c['row']['id'], $all['changes']);
    $versions = array_map(fn ($c) => $c['version'], $all['changes']);
    expect($ids)->toContain($unit, $brand, $own)->not->toContain($foreign)
        ->and($versions)->toBe(collect($versions)->sort()->values()->all())
        ->and($all['has_more'])->toBeFalse()
        ->and($all['next_since'])->toBe($all['safe_version']);
    $deleted = collect($all['changes'])->firstWhere('row.id', $brand);
    expect($deleted['table'])->toBe('brands')->and($deleted['row']['deleted_at'])->not->toBeNull();
    $setting = collect($all['changes'])->firstWhere('row.id', $own);
    expect($setting['row'])->not->toHaveKey('branch_key');                // generated columns never travel

    // Page by page with limit 1: the same rows, in the same order, nothing skipped or repeated.
    $paged = [];
    $cursor = $since;
    for ($i = 0; $i < 100; $i++) {
        $page = $this->withToken($node->token)->getJson("/api/v1/sync/pull?since={$cursor}&limit=1")->assertOk()->json();
        foreach ($page['changes'] as $c) {
            $paged[] = $c['row']['id'];
        }
        $cursor = $page['next_since'];
        if (! $page['has_more']) {
            break;
        }
    }
    expect($paged)->toBe($ids);

    $this->withToken($node->token)->getJson('/api/v1/sync/pull?since=-1')->assertStatus(422);
    $this->withToken($node->token)->getJson('/api/v1/sync/pull?limit=5000')->assertStatus(422);
});

it('never skips a version that is still being written (safe cursor)', function () {
    $node = syncNode();
    $config = config('database.connections.mysql');
    $pdo = new PDO("mysql:host={$config['host']};port={$config['port']};dbname={$config['database']}", $config['username'], $config['password']);
    $pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
    $pdo->exec("SET time_zone = '+00:00'");

    // A writer in another connection takes the next version like MasterDataWriter and has not committed yet.
    $pdo->beginTransaction();
    $version = (int) $pdo->query('SELECT last_version FROM master_version WHERE id = 1 FOR UPDATE')->fetchColumn() + 1;
    $pdo->exec("UPDATE master_version SET last_version = {$version} WHERE id = 1");
    $unit = id7();
    $code = uid('U');
    $pdo->exec("INSERT INTO units (id, code, name, version) VALUES ('{$unit}', '{$code}', 'Belum commit', {$version})");

    $before = $this->withToken($node->token)->getJson('/api/v1/sync/pull?since='.($version - 1))->assertOk()->json();
    expect($before['safe_version'])->toBe($version - 1)
        ->and($before['next_since'])->toBe($version - 1)        // the cursor stays below the version in flight
        ->and($before['changes'])->toBe([]);

    $pdo->commit();
    $after = $this->withToken($node->token)->getJson("/api/v1/sync/pull?since={$before['next_since']}")->assertOk()->json();
    expect(array_map(fn ($c) => $c['row']['id'], $after['changes']))->toContain($unit)
        ->and($after['safe_version'])->toBeGreaterThanOrEqual($version);
});

// ---- push --------------------------------------------------------------------------------------------

it('stores a shift and a sale as copies, idempotently', function () {
    $node = syncNode();
    $cashier = makeUser()->id;
    $product = syncProduct();
    [$shift, $device, $sale] = [id7(), id7(), id7()];

    push($node, [shiftDoc($node, $shift, $device, $cashier), saleDoc($node, $sale, $shift, $cashier, $product)])
        ->assertOk()->assertJsonPath('results.0.status', 'accepted')->assertJsonPath('results.1.status', 'accepted')
        ->assertJsonPath('results.1.outbox_id', 2)->assertJsonPath('results.1.doc_id', $sale);

    expect(DB::table('sales')->where('id', $sale)->value('total'))->toBe('8000.0000')
        ->and(DB::table('sale_lines')->where('sale_id', $sale)->count())->toBe(1)
        ->and(DB::table('stock_ledger')->where('doc_id', $sale)->value('base_qty'))->toBe('-2.0000')
        ->and(DB::table('devices')->where('id', $device)->value('node_id'))->toBe($node->id)
        ->and(DB::table('inbound_documents')->where('doc_id', $sale)->first())->toMatchArray(['doc_type' => 'SALE', 'node_id' => $node->id, 'row_version' => 1, 'outbox_id' => 2]);

    // Sent again (the node did not get the answer): nothing changes.
    push($node, [saleDoc($node, $sale, $shift, $cashier, $product, total: '9999.0000')])->assertJsonPath('results.0.status', 'duplicate');
    expect(DB::table('sales')->where('id', $sale)->value('total'))->toBe('8000.0000')
        ->and(DB::table('sale_lines')->where('sale_id', $sale)->count())->toBe(1);
});

it('keeps the highest row_version of a mutable document and ignores older ones', function () {
    $node = syncNode();
    $cashier = makeUser()->id;
    [$shift, $device] = [id7(), id7()];

    push($node, [shiftDoc($node, $shift, $device, $cashier, 1, 'OPEN', 1)])->assertJsonPath('results.0.status', 'accepted');
    push($node, [shiftDoc($node, $shift, $device, $cashier, 2, 'CLOSED', 5)])->assertJsonPath('results.0.status', 'accepted');
    expect(DB::table('cashier_shifts')->where('id', $shift)->value('status'))->toBe('CLOSED');

    push($node, [shiftDoc($node, $shift, $device, $cashier, 1, 'OPEN', 1)])->assertJsonPath('results.0.status', 'stale');
    expect(DB::table('cashier_shifts')->where('id', $shift)->value('status'))->toBe('CLOSED')
        ->and((int) DB::table('inbound_documents')->where('doc_id', $shift)->value('row_version'))->toBe(2)
        ->and((int) DB::table('inbound_documents')->where('doc_id', $shift)->value('outbox_id'))->toBe(5);
});

it('rejects documents and rows of another node, one document at a time', function () {
    $node = syncNode();
    $other = syncNode();
    $cashier = makeUser()->id;
    $product = syncProduct();
    [$shift, $device] = [id7(), id7()];

    $foreign = shiftDoc($other, id7(), id7(), $cashier, outbox: 2);              // other node's document with my token
    $sneaky = shiftDoc($node, id7(), id7(), $cashier, outbox: 3);
    $sneaky['payload']['tables'][1]['rows'][0]['node_id'] = $other->id;          // root row claims the other node

    $results = push($node, [shiftDoc($node, $shift, $device, $cashier), $foreign, $sneaky, saleDoc($node, id7(), $shift, $cashier, $product, 4)])
        ->assertOk()->json('results');
    expect(array_column($results, 'status'))->toBe(['accepted', 'rejected', 'rejected', 'accepted'])
        ->and($results[1])->toMatchArray(['code' => 'foreign_node', 'retry' => false])
        ->and($results[2]['code'])->toBe('foreign_node');

    // The other node pushes its own shift; my node may not overwrite it by reusing its id.
    $otherShift = id7();
    push($other, [shiftDoc($other, $otherShift, id7(), $cashier)])->assertJsonPath('results.0.status', 'accepted');
    $hijack = shiftDoc($node, $otherShift, $device, $cashier, 3, 'CLOSED', 9);
    push($node, [$hijack])->assertJsonPath('results.0.status', 'rejected')->assertJsonPath('results.0.code', 'foreign_row');
    expect(DB::table('cashier_shifts')->where('id', $otherShift)->value('node_id'))->toBe($other->id);
});

it('rejects rows that do not belong to the document, unknown tables and columns', function () {
    $node = syncNode();
    $cashier = makeUser()->id;
    $product = syncProduct();
    $shift = id7();
    push($node, [shiftDoc($node, $shift, id7(), $cashier)]);

    $wrongLine = saleDoc($node, id7(), $shift, $cashier, $product, 2);
    $wrongLine['payload']['tables'][1]['rows'][0]['sale_id'] = id7();
    $unknownTable = saleDoc($node, id7(), $shift, $cashier, $product, 3);
    $unknownTable['payload']['tables'][] = ['table' => 'users', 'rows' => [['id' => $cashier, 'username' => 'hacker']]];
    $unknownColumn = saleDoc($node, id7(), $shift, $cashier, $product, 4);
    $unknownColumn['payload']['tables'][0]['rows'][0]['is_admin'] = 1;
    $generated = shiftDoc($node, id7(), id7(), $cashier, outbox: 5);
    $generated['doc_type'] = 'SETTINGS';

    $results = push($node, [$wrongLine, $unknownTable, $unknownColumn, $generated])->json('results');
    expect(array_column($results, 'code'))->toBe(['foreign_row', 'unknown_table', 'unknown_column', 'unknown_doc_type'])
        ->and(DB::table('users')->where('id', $cashier)->value('username'))->not->toBe('hacker');
});

it('asks to retry later when a master row is missing at HQ', function () {
    $node = syncNode();
    $cashier = makeUser()->id;
    $shift = id7();
    push($node, [shiftDoc($node, $shift, id7(), $cashier)]);

    $sale = id7();
    $result = push($node, [saleDoc($node, $sale, $shift, $cashier, [id7(), id7()], 2)])->json('results.0');
    expect($result)->toMatchArray(['status' => 'rejected', 'code' => 'missing_reference', 'retry' => true])
        ->and(DB::table('sales')->where('id', $sale)->exists())->toBeFalse()          // all or nothing per document
        ->and(DB::table('inbound_documents')->where('doc_id', $sale)->exists())->toBeFalse();
});

it('limits batch size and body size, and validates the request', function () {
    $node = syncNode();
    $this->withToken($node->token)->postJson('/api/v1/sync/push', ['documents' => array_fill(0, 201, [])])
        ->assertStatus(413)->assertJson(['code' => 'batch_too_large']);
    $this->withToken($node->token)->postJson('/api/v1/sync/push', ['nothing' => true])->assertStatus(422);

    config(['pos.sync.max_body_bytes' => 2000]);
    $this->withToken($node->token)->postJson('/api/v1/sync/push', ['documents' => [['pad' => str_repeat('x', 3000)]]])
        ->assertStatus(413);
    $big = gzencode((string) json_encode(['documents' => [['pad' => str_repeat('x', 5000)]]]));
    $this->call('POST', '/api/v1/sync/push', [], [], [], [
        'HTTP_AUTHORIZATION' => 'Bearer '.$node->token, 'CONTENT_TYPE' => 'application/json', 'HTTP_CONTENT_ENCODING' => 'gzip',
    ], $big)->assertStatus(413);
});

it('accepts gzip requests and compresses responses', function () {
    $node = syncNode();
    $cashier = makeUser()->id;
    $shift = id7();
    $body = gzencode((string) json_encode(['documents' => [shiftDoc($node, $shift, id7(), $cashier)]]));

    $response = $this->call('POST', '/api/v1/sync/push', [], [], [], [
        'HTTP_AUTHORIZATION' => 'Bearer '.$node->token, 'CONTENT_TYPE' => 'application/json', 'HTTP_ACCEPT' => 'application/json',
        'HTTP_CONTENT_ENCODING' => 'gzip',
    ], $body)->assertOk();
    expect($response->json('results.0.status'))->toBe('accepted');

    $pulled = $this->withToken($node->token)->withHeaders(['Accept-Encoding' => 'gzip'])->get('/api/v1/sync/pull?since=0&limit=50')->assertOk();
    expect($pulled->headers->get('Content-Encoding'))->toBe('gzip')
        ->and(json_decode((string) gzdecode((string) $pulled->getContent()), true)['changes'])->toHaveCount(50);

    $this->call('POST', '/api/v1/sync/push', [], [], [], [
        'HTTP_AUTHORIZATION' => 'Bearer '.$node->token, 'CONTENT_TYPE' => 'application/json', 'HTTP_CONTENT_ENCODING' => 'gzip',
    ], 'bukan gzip')->assertStatus(400);
});

it('records heartbeats with the clock skew and when the node was seen', function () {
    $node = syncNode();
    $nodeTime = now('UTC')->addSeconds(90)->format('Y-m-d H:i:s.v');

    $response = $this->withToken($node->token)->postJson('/api/v1/sync/heartbeat', [
        'app_version' => '1.2.0', 'schema_version' => '0012_sync.sql', 'master_version' => 77, 'pending_outbox' => 3,
        'failed_outbox' => 1, 'node_time' => $nodeTime,
    ])->assertOk();
    expect($response->json('clock_skew_ms'))->toBeGreaterThan(85000)->toBeLessThan(95000);

    $row = DB::table('node_heartbeats')->where('node_id', $node->id)->first();
    expect($row->app_version)->toBe('1.2.0')->and((int) $row->master_version)->toBe(77)->and((int) $row->pending_outbox)->toBe(3)
        ->and((int) $row->failed_outbox)->toBe(1)->and($row->last_seen_at)->not->toBeNull()->and($row->last_ip)->toBe('127.0.0.1');

    $this->withToken($node->token)->postJson('/api/v1/sync/heartbeat', ['pending_outbox' => -1])->assertStatus(422);
});

it('rate limits each node separately', function () {
    config(['pos.sync.rate_per_minute' => 2]);
    $a = syncNode();
    $b = syncNode();

    $this->withToken($a->token)->getJson('/api/v1/sync/pull?limit=1')->assertOk();
    $this->withToken($a->token)->getJson('/api/v1/sync/pull?limit=1')->assertOk();
    $this->withToken($a->token)->getJson('/api/v1/sync/pull?limit=1')->assertStatus(429)->assertJson(['code' => 'rate_limited']);
    $this->withToken($b->token)->getJson('/api/v1/sync/pull?limit=1')->assertOk();
});
