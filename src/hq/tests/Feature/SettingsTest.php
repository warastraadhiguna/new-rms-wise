<?php

use App\Http\Controllers\Settings\SettingController;
use App\Models\User;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use App\Support\Printing\ReceiptFormatter;
use App\Support\Settings\SettingCatalog;
use Illuminate\Support\Facades\DB;
use Inertia\Testing\AssertableInertia as Assert;

/*
| Pengaturan (12c): global values and per-branch overrides, validated by type and range, written through the master
| data writer only when they change; the receipt preview uses the PHP port of the node's formatter.
|
| Global rows are shared by the whole test database, so tests that write them run inside a transaction that is rolled
| back (the HTTP requests use the same connection).
*/

function settingsAdmin(): User
{
    return editor(['hq.access', 'settings.manage']);
}

/** Every field with a valid value, as the page submits it (global scope). */
function validSettings(array $overrides = []): array
{
    $values = [];
    foreach (SettingCatalog::items() as $item) {
        $values[$item['field']] = match ($item['type']) {
            SettingCatalog::BOOLEAN => $item['default'] ? '1' : '0',
            default => (string) $item['default'],
        };
    }

    return ['branch_id' => '', 'values' => array_merge($values, $overrides)];
}

function settingRow(?string $branch, string $key): ?object
{
    return DB::table('settings')->where('setting_key', $key)
        ->when($branch === null, fn ($q) => $q->whereNull('branch_id'), fn ($q) => $q->where('branch_id', $branch))
        ->first(['id', 'deleted_at', 'version', DB::raw('CAST(setting_value AS CHAR) AS setting_value')]);
}

function inRollback(Closure $test): void
{
    DB::beginTransaction();
    try {
        $test();
    } finally {
        DB::rollBack();
    }
}

it('saves global settings with the types the node reads, and only what changed', function () {
    $this->actingAs(settingsAdmin());

    inRollback(function () {
        DB::table('settings')->whereNull('branch_id')->delete();

        $this->put('/pengaturan/settings', validSettings([
            'receipt_store_name' => '  TOKO MAJU  ', 'rounding_cash_step' => '100.00', 'rounding_cash_mode' => 'up',
            'currency_decimals' => '2', 'returns_window_days' => '14', 'shift_blind_count' => '0', 'pricing_uniform' => '1',
        ]))->assertSessionHasNoErrors();

        expect(settingRow(null, 'receipt.store_name')->setting_value)->toBe('"TOKO MAJU"')
            ->and(settingRow(null, 'rounding.cash_step')->setting_value)->toBe('"100"')
            ->and(settingRow(null, 'rounding.cash_mode')->setting_value)->toBe('"up"')
            ->and(settingRow(null, 'currency.decimals')->setting_value)->toBe('2')
            ->and(settingRow(null, 'returns.window_days')->setting_value)->toBe('14')
            ->and(settingRow(null, 'shift.blind_count')->setting_value)->toBe('false')
            ->and(settingRow(null, 'pricing.uniform')->setting_value)->toBe('true')
            ->and(settingRow(null, 'stock.expiry_warning_days'))->toBeNull()     // default and never stored: left missing
            ->and(lastAudit('settings', settingRow(null, 'currency.decimals')->id)->action)->toBe('CREATE');

        $version = (int) settingRow(null, 'currency.decimals')->version;
        $this->put('/pengaturan/settings', validSettings([
            'receipt_store_name' => 'TOKO MAJU', 'rounding_cash_step' => '100', 'rounding_cash_mode' => 'up',
            'currency_decimals' => '2', 'returns_window_days' => '30', 'shift_blind_count' => '0', 'pricing_uniform' => '1',
        ]))->assertSessionHasNoErrors();

        expect((int) settingRow(null, 'currency.decimals')->version)->toBe($version)     // unchanged: no new version
            ->and(settingRow(null, 'returns.window_days')->setting_value)->toBe('30')
            ->and(lastAudit('settings', settingRow(null, 'returns.window_days')->id)->action)->toBe('UPDATE');
    });
});

it('validates every value by type and range', function (string $field, string $value) {
    $this->actingAs(settingsAdmin());
    $this->put('/pengaturan/settings', validSettings([$field => $value]))->assertSessionHasErrors("values.{$field}");
})->with([
    ['rounding_cash_step', '-100'],
    ['rounding_cash_step', 'abc'],
    ['rounding_cash_step', '100.12345'],
    ['rounding_cash_step', '100001'],
    ['rounding_cash_mode', 'sideways'],
    ['currency_decimals', '5'],
    ['currency_decimals', '1.5'],
    ['sales_cashier_history_days', '-1'],
    ['returns_window_days', '366'],
    ['stock_expiry_warning_days', 'tiga puluh'],
    ['shift_blind_count', 'mungkin'],
    ['pricing_uniform', '2'],
    ['receipt_store_name', str_repeat('A', 101)],
]);

it('overrides a value per branch and removes the override again', function () {
    $this->actingAs(settingsAdmin());
    $branch = newBranch();
    $request = fn (array $values, array $overrides) => ['branch_id' => $branch, 'values' => $values, 'overrides' => $overrides];

    $this->put('/pengaturan/settings', $request(
        ['receipt_store_name' => 'TOKO CABANG', 'returns_window_days' => '3', 'currency_decimals' => '9'],
        ['receipt_store_name' => true, 'returns_window_days' => true, 'currency_decimals' => false],
    ))->assertSessionHasNoErrors();      // not overridden: its value is not validated nor stored

    expect(settingRow($branch, 'receipt.store_name')->setting_value)->toBe('"TOKO CABANG"')
        ->and(settingRow($branch, 'returns.window_days')->setting_value)->toBe('3')
        ->and(settingRow($branch, 'currency.decimals'))->toBeNull()
        ->and(settingRow($branch, 'pricing.uniform'))->toBeNull();

    $this->get("/pengaturan/settings?branch={$branch}")->assertOk()
        ->assertInertia(fn (Assert $page) => $page->component('settings/settings')->where('branchId', $branch)
            ->where('values', fn ($values) => $values['returns.window_days']['branch'] === 3
                && $values['returns.window_days']['overridden'] === true
                && $values['currency.decimals']['overridden'] === false));

    // Stop overriding: soft delete; override again: the same row comes back (unique branch_id + key).
    $row = settingRow($branch, 'returns.window_days');
    $this->put('/pengaturan/settings', $request(['receipt_store_name' => 'TOKO CABANG'], ['receipt_store_name' => true]))->assertSessionHasNoErrors();
    expect(settingRow($branch, 'returns.window_days')->deleted_at)->not->toBeNull()
        ->and(lastAudit('settings', $row->id)->action)->toBe('DELETE');

    $this->put('/pengaturan/settings', $request(['receipt_store_name' => 'TOKO CABANG', 'returns_window_days' => '5'], ['receipt_store_name' => true, 'returns_window_days' => true]))
        ->assertSessionHasNoErrors();
    $back = settingRow($branch, 'returns.window_days');
    expect($back->id)->toBe($row->id)->and($back->deleted_at)->toBeNull()->and($back->setting_value)->toBe('5')
        ->and(lastAudit('settings', $row->id)->action)->toBe('RESTORE');

    $this->put('/pengaturan/settings', $request(['returns_window_days' => '400'], ['returns_window_days' => true]))
        ->assertSessionHasErrors('values.returns_window_days');
});

it('reads stored values tolerantly, like the node', function () {
    $item = SettingCatalog::items()['shift.blind_count'];
    $int = SettingCatalog::items()['returns.window_days'];
    $mode = SettingCatalog::items()['rounding.cash_mode'];

    expect(SettingCatalog::decode($item, '"true"'))->toBeTrue()
        ->and(SettingCatalog::decode($item, '0'))->toBeFalse()
        ->and(SettingCatalog::decode($item, '"x"'))->toBeNull()
        ->and(SettingCatalog::decode($int, '"14"'))->toBe(14)
        ->and(SettingCatalog::decode($int, null))->toBeNull()
        ->and(SettingCatalog::decode($mode, '"sideways"'))->toBeNull();
});

it('previews the receipt exactly as the node formatter prints it', function () {
    $this->actingAs(settingsAdmin());

    $response = $this->getJson('/pengaturan/settings/receipt-preview?'.http_build_query([
        'store_name' => 'TOKO MAJU', 'address' => 'Jl. Merdeka 1', 'city' => '', 'phone' => '022-123', 'footer_1' => 'Terima kasih', 'footer_2' => '',
    ]))->assertOk();

    $expected = ReceiptFormatter::format(SettingController::sample(['TOKO MAJU', 'Jl. Merdeka 1', 'Telp. 022-123'], ['Terima kasih']));
    expect($response->json('lines'))->toBe($expected)
        ->and($expected[0])->toBe(str_repeat(' ', 19).'TOKO MAJU')
        ->and(max(array_map('mb_strlen', $expected)))->toBeLessThanOrEqual(48);
});

it('needs settings.manage', function () {
    $this->actingAs(editor(['hq.access', 'users.manage']));
    $this->get('/pengaturan/settings')->assertForbidden();
    $this->put('/pengaturan/settings', [])->assertForbidden();
    $this->get('/pengaturan/settings/receipt-preview')->assertForbidden();
    $this->get('/pengaturan/payment-methods')->assertForbidden();
});

it('keeps pricing.uniform global: a branch cannot override it', function () {
    $this->actingAs(settingsAdmin());
    $branch = newBranch();
    $this->put('/pengaturan/settings', ['branch_id' => $branch, 'values' => ['pricing_uniform' => '1'], 'overrides' => ['pricing_uniform' => true]])
        ->assertSessionHasNoErrors();
    expect(settingRow($branch, 'pricing.uniform'))->toBeNull();

    app(MasterDataWriter::class)->create('settings', ['branch_id' => $branch, 'setting_key' => 'stock.expiry_warning_days', 'setting_value' => '"45"'], WriteContext::system());
    $this->get("/pengaturan/settings?branch={$branch}")->assertInertia(fn (Assert $page) => $page
        ->where('values', fn ($values) => $values['stock.expiry_warning_days']['branch'] === 45));
});
