<?php

namespace App\Support\Monitoring;

use App\Support\Decimal;
use App\Support\Settings\SettingCatalog;
use Carbon\CarbonImmutable;
use Illuminate\Support\Facades\DB;

/**
 * The HQ dashboard: every store and warehouse with its sync state (from node_heartbeats, written by the sync API) and
 * today's sales per store from the copies the nodes pushed (branch-local "today", voided sales left out, returns
 * of the day shown separately).
 */
final class NodeMonitor
{
    public const ALERT_SETTING = 'sync.alert_minutes';

    /** @return list<array<string, mixed>> */
    public function nodes(CarbonImmutable $now): array
    {
        $master = (int) (DB::table('master_version')->where('id', 1)->value('last_version') ?? 0);
        $rows = DB::table('nodes as n')
            ->join('branches as b', 'b.id', '=', 'n.branch_id')
            ->leftJoin('node_heartbeats as h', 'h.node_id', '=', 'n.id')
            ->whereNull('n.deleted_at')
            ->where('n.is_active', true)
            ->where('n.type', '!=', 'HQ')
            ->orderBy('n.code')
            ->get(['n.id', 'n.code', 'n.name', 'n.type', 'n.branch_id', 'b.name as branch_name', 'b.timezone',
                'h.last_seen_at', 'h.pending_outbox', 'h.failed_outbox', 'h.master_version', 'h.clock_skew_ms', 'h.app_version',
                'h.last_push_at', 'h.last_pull_at']);

        $result = [];
        foreach ($rows as $row) {
            $r = (array) $row;
            $lastSeen = $r['last_seen_at'] === null ? null : CarbonImmutable::parse((string) $r['last_seen_at'], 'UTC');
            $alert = $this->alertMinutes((string) $r['branch_id']);
            $health = NodeHealth::of($lastSeen, $now, $alert, (int) ($r['failed_outbox'] ?? 0),
                $r['clock_skew_ms'] === null ? null : (int) $r['clock_skew_ms']);
            $result[] = [
                'id' => (string) $r['id'],
                'code' => (string) $r['code'],
                'name' => (string) $r['name'],
                'type' => (string) $r['type'],
                'branch_name' => (string) $r['branch_name'],
                'last_seen_at' => $lastSeen?->format('Y-m-d H:i:s'),
                'minutes_since_seen' => $lastSeen === null ? null : (int) floor(max(0, $lastSeen->diffInSeconds($now, false)) / 60),
                'pending_outbox' => $r['pending_outbox'] === null ? null : (int) $r['pending_outbox'],
                'failed_outbox' => $r['failed_outbox'] === null ? null : (int) $r['failed_outbox'],
                'master_lag' => $r['master_version'] === null ? null : max(0, $master - (int) $r['master_version']),
                'clock_skew_seconds' => $r['clock_skew_ms'] === null ? null : (int) round(((int) $r['clock_skew_ms']) / 1000),
                'app_version' => $r['app_version'] === null ? null : (string) $r['app_version'],
                'alert_minutes' => $alert,
                'status' => $health['status'],
                'reasons' => $health['reasons'],
            ];
        }

        return $result;
    }

    /**
     * Today's sales per store as far as they reached HQ.
     *
     * @return list<array{node_id: string, code: string, name: string, date: string, sale_count: int, sales_total: string, return_total: string, net_total: string}>
     */
    public function salesToday(CarbonImmutable $now): array
    {
        $stores = DB::table('nodes as n')->join('branches as b', 'b.id', '=', 'n.branch_id')
            ->whereNull('n.deleted_at')->where('n.type', 'STORE')->orderBy('n.code')
            ->get(['n.id', 'n.code', 'n.name', 'b.timezone']);

        $result = [];
        foreach ($stores as $store) {
            $s = (array) $store;
            $tz = (string) ($s['timezone'] ?: config('pos.display_timezone'));
            $local = $now->setTimezone($tz);
            $from = $local->startOfDay()->utc()->format('Y-m-d H:i:s.v');
            $to = $local->startOfDay()->addDay()->utc()->format('Y-m-d H:i:s.v');

            $sales = DB::table('sales as s')
                ->where('s.node_id', $s['id'])->where('s.sold_at', '>=', $from)->where('s.sold_at', '<', $to)
                ->whereNotExists(fn ($q) => $q->from('sale_voids as v')->whereColumn('v.sale_id', 's.id'))
                ->selectRaw('COUNT(*) AS n, COALESCE(SUM(s.total), 0) AS total')->first();
            $returns = DB::table('sale_returns')
                ->where('node_id', $s['id'])->where('returned_at', '>=', $from)->where('returned_at', '<', $to)
                ->selectRaw('COALESCE(SUM(total), 0) AS total')->value('total');

            $salesRow = (array) $sales;
            $salesTotal = Decimal::toString((string) ($salesRow['total'] ?? '0'));
            $returnTotal = Decimal::toString((string) ($returns ?? '0'));
            $result[] = [
                'node_id' => (string) $s['id'],
                'code' => (string) $s['code'],
                'name' => (string) $s['name'],
                'date' => $local->format('Y-m-d'),
                'sale_count' => (int) ($salesRow['n'] ?? 0),
                'sales_total' => $salesTotal,
                'return_total' => $returnTotal,
                'net_total' => Decimal::toString(Decimal::of($salesTotal)->minus(Decimal::of($returnTotal))),
            ];
        }

        return $result;
    }

    /** The branch's sync.alert_minutes, else the global one, else 15 (stored as a JSON number). */
    private function alertMinutes(string $branchId): int
    {
        $values = DB::table('settings')->where('setting_key', self::ALERT_SETTING)->whereNull('deleted_at')
            ->where(fn ($q) => $q->whereNull('branch_id')->orWhere('branch_id', $branchId))
            ->orderByRaw('branch_id IS NULL')
            ->selectRaw('CAST(setting_value AS CHAR) AS v')
            ->pluck('v');
        $item = SettingCatalog::items()[self::ALERT_SETTING] ?? null;
        foreach ($values as $json) {
            $value = $item === null ? null : SettingCatalog::decode($item, is_string($json) ? $json : null);
            if (is_int($value) && $value > 0) {
                return $value;
            }
        }

        return NodeHealth::DEFAULT_ALERT_MINUTES;
    }
}
