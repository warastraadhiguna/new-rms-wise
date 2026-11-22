<?php

namespace App\Http\Controllers\Settings;

use App\Http\Controllers\Controller;
use App\Support\MasterData\MasterDataWriter;
use App\Support\Printing\ReceiptFormatter;
use App\Support\Settings\SettingCatalog;
use Illuminate\Http\JsonResponse;
use Illuminate\Http\RedirectResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Validation\Rule;
use Inertia\Inertia;
use Inertia\Response;

/**
 * Pengaturan: global values (settings.branch_id NULL) and per-branch overrides (the branch row wins at the node).
 * Saving writes only what changed, through the master data writer: a new row, an update, a restored row (the unique key
 * (branch_id, setting_key) keeps deleted rows), or a soft delete when a branch stops overriding a key.
 */
class SettingController extends Controller
{
    public function index(Request $request): Response
    {
        $branchId = $this->branch($request->query('branch'));
        $global = $this->stored(null);
        $branch = $branchId === null ? [] : $this->stored($branchId);

        $values = [];
        foreach (SettingCatalog::items() as $key => $item) {
            $values[$key] = [
                'global' => SettingCatalog::decode($item, $global[$key] ?? null),
                'branch' => SettingCatalog::decode($item, $branch[$key] ?? null),
                'overridden' => array_key_exists($key, $branch),
            ];
        }

        return Inertia::render('settings/settings', [
            'groups' => SettingCatalog::groups(),
            'values' => $values,
            'branches' => DB::table('branches')->whereNull('deleted_at')->orderBy('code')->get(['id', 'code', 'name']),
            'branchId' => $branchId,
            'receiptWidth' => ReceiptFormatter::WIDTH_80MM,
        ]);
    }

    public function update(Request $request, MasterDataWriter $writer): RedirectResponse
    {
        $branchId = $this->branch($request->input('branch_id'));
        $items = array_filter(SettingCatalog::items(), fn (array $item) => $branchId === null || ! $item['globalOnly']);

        $rules = ['branch_id' => ['nullable', 'string']];
        $labels = [];
        foreach ($items as $item) {
            $field = $item['field'];
            $overridden = $branchId === null || $request->boolean("overrides.{$field}");
            $rules["values.{$field}"] = $overridden ? SettingCatalog::rules($item) : ['nullable'];
            $labels["values.{$field}"] = $item['label'];
        }
        $data = $request->validate($rules, [], $labels);

        $global = $this->stored(null);
        DB::transaction(function () use ($items, $data, $request, $branchId, $writer, $global) {
            foreach ($items as $key => $item) {
                $field = $item['field'];
                $value = $data['values'][$field] ?? null;
                if ($branchId === null) {
                    $json = SettingCatalog::encode($item, $value);
                    // Nothing stored and the default chosen: leave it missing (the node uses the same default).
                    if (! array_key_exists($key, $global) && json_decode($json, true) === $item['default']) {
                        continue;
                    }
                    $this->put($writer, null, $key, $json);
                } elseif ($request->boolean("overrides.{$field}")) {
                    $this->put($writer, $branchId, $key, SettingCatalog::encode($item, $value));
                } else {
                    $this->put($writer, $branchId, $key, null);
                }
            }
        });

        return $this->saved($branchId === null ? 'Pengaturan global disimpan.' : 'Pengaturan cabang disimpan.');
    }

    /** The receipt preview: a fixed sample sale with the header/footer being edited, the node's exact layout. */
    public function receiptPreview(Request $request): JsonResponse
    {
        $data = $request->validate([
            'store_name' => ['nullable', 'string', 'max:100'],
            'address' => ['nullable', 'string', 'max:200'],
            'city' => ['nullable', 'string', 'max:100'],
            'phone' => ['nullable', 'string', 'max:50'],
            'footer_1' => ['nullable', 'string', 'max:100'],
            'footer_2' => ['nullable', 'string', 'max:100'],
        ]);
        $text = fn (string $key): ?string => isset($data[$key]) ? (string) $data[$key] : null;

        return response()->json(['lines' => ReceiptFormatter::format(self::sample(
            ReceiptFormatter::header($text('store_name'), $text('address'), $text('city'), $text('phone')),
            ReceiptFormatter::footer($text('footer_1'), $text('footer_2')),
        ))]);
    }

    /**
     * @param  list<string>  $header
     * @param  list<string>  $footer
     * @return array<string, mixed>
     */
    public static function sample(array $header, array $footer): array
    {
        return [
            'header' => $header,
            'footer' => $footer,
            'number' => 'TK01-SL-202611-000123',
            'sold_at_local' => '2026-11-22 14:05',
            'cashier' => 'Kasir Satu',
            'customer' => null,
            'items' => [
                ['name' => 'Teh Botol Sosro 450 ml', 'qty' => '3', 'unit' => 'PCS', 'unit_price' => '4000', 'line_total' => '12000'],
                ['name' => 'Gula Pasir', 'qty' => '0.25', 'unit' => 'KG', 'unit_price' => '16000', 'line_total' => '4000'],
                ['name' => 'Indomie Goreng', 'qty' => '1', 'unit' => 'DUS', 'unit_price' => '115000', 'line_total' => '115000'],
            ],
            'subtotal' => '131000',
            'discount_amount' => '0',
            'discount_pct' => null,
            'rounding' => '0',
            'total' => '131000',
            'payments' => [['method' => 'TUNAI', 'is_cash' => true, 'is_credit' => false, 'amount' => '131000', 'tendered' => '150000', 'change' => '19000']],
            'due_date' => null,
        ];
    }

    /** Creates, updates, restores or (json null) soft-deletes one row — only when something changes. */
    private function put(MasterDataWriter $writer, ?string $branchId, string $key, ?string $json): void
    {
        $row = DB::table('settings')->where('setting_key', $key)
            ->when($branchId === null, fn ($q) => $q->whereNull('branch_id'), fn ($q) => $q->where('branch_id', $branchId))
            ->orderByRaw('deleted_at IS NULL DESC')
            ->first(['id', 'deleted_at', DB::raw('CAST(setting_value AS CHAR) AS setting_value')]);

        if ($json === null) {
            if ($row !== null && $row->deleted_at === null) {
                $writer->delete('settings', (string) $row->id);
            }

            return;
        }
        if ($row === null) {
            $writer->create('settings', ['branch_id' => $branchId, 'setting_key' => $key, 'setting_value' => $json]);
        } elseif ($row->deleted_at !== null) {
            $writer->restore('settings', (string) $row->id, ['setting_value' => $json]);
        } elseif (json_decode((string) $row->setting_value, true) !== json_decode($json, true)) {
            $writer->update('settings', (string) $row->id, ['setting_value' => $json]);
        }
    }

    /** @return array<string, string> active rows of one scope: key => JSON */
    private function stored(?string $branchId): array
    {
        return DB::table('settings')->whereNull('deleted_at')
            ->when($branchId === null, fn ($q) => $q->whereNull('branch_id'), fn ($q) => $q->where('branch_id', $branchId))
            ->selectRaw('setting_key, CAST(setting_value AS CHAR) AS setting_value')
            ->pluck('setting_value', 'setting_key')
            ->map(fn (mixed $v) => (string) $v)
            ->all();
    }

    private function branch(mixed $id): ?string
    {
        if ($id === null || $id === '') {
            return null;
        }
        validator(['branch' => $id], ['branch' => ['string', Rule::exists('branches', 'id')->whereNull('deleted_at')]])->validate();

        return (string) $id;
    }
}
