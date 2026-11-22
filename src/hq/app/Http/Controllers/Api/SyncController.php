<?php

namespace App\Http\Controllers\Api;

use App\Http\Controllers\Controller;
use App\Http\Middleware\SyncCompression;
use App\Sync\DocumentPush;
use App\Sync\MasterPull;
use App\Sync\NodeTokens;
use App\Sync\SyncNode;
use Carbon\CarbonImmutable;
use Illuminate\Http\JsonResponse;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;

/** /api/v1/sync — see docs/sync-contract.md. Every action acts for the node of the bearer token. */
class SyncController extends Controller
{
    public function pull(Request $request, MasterPull $pull): JsonResponse
    {
        $since = filter_var($request->query('since', '0'), FILTER_VALIDATE_INT, ['options' => ['min_range' => 0]]);
        $limit = filter_var($request->query('limit', (string) config('pos.sync.pull_default_limit')), FILTER_VALIDATE_INT,
            ['options' => ['min_range' => 1, 'max_range' => (int) config('pos.sync.pull_max_limit')]]);
        if ($since === false || $limit === false) {
            return $this->invalid('since harus bilangan bulat >= 0, limit 1 sampai '.config('pos.sync.pull_max_limit').'.');
        }
        $node = $this->node($request);
        $result = $pull->pull($node, $since, $limit);
        DB::table('node_heartbeats')->where('node_id', $node->id)->update(['last_pull_at' => NodeTokens::now()]);
        // Asking for changes after $since means the node already has everything up to it.
        DB::table('node_heartbeats')->where('node_id', $node->id)->where('master_version', '<', $since)->update(['master_version' => $since]);

        return response()->json($result);
    }

    public function push(Request $request, DocumentPush $push): JsonResponse
    {
        $body = SyncCompression::json($request);
        $documents = is_array($body) ? ($body['documents'] ?? null) : null;
        if (! is_array($documents) || ! array_is_list($documents)) {
            return $this->invalid('Body harus berisi "documents": [...].');
        }
        $max = (int) config('pos.sync.push_max_documents');
        if (count($documents) > $max) {
            return response()->json(['code' => 'batch_too_large', 'message' => "Maksimal {$max} dokumen per request."], 413);
        }

        return response()->json(['results' => $push->push($this->node($request), $documents)]);
    }

    public function heartbeat(Request $request): JsonResponse
    {
        $body = SyncCompression::json($request);
        $body = is_array($body) ? $body : [];
        $validator = validator($body, [
            'app_version' => ['nullable', 'string', 'max:20'],
            'schema_version' => ['nullable', 'string', 'max:40'],
            'master_version' => ['required', 'integer', 'min:0'],
            'pending_outbox' => ['required', 'integer', 'min:0'],
            'failed_outbox' => ['nullable', 'integer', 'min:0'],
            'node_time' => ['required', 'date'],
        ]);
        if ($validator->fails()) {
            return $this->invalid((string) $validator->errors()->first());
        }

        $node = $this->node($request);
        $hq = CarbonImmutable::now('UTC');
        $nodeTime = CarbonImmutable::parse((string) $body['node_time'], 'UTC')->utc();
        $skew = (int) max(-2147483648, min(2147483647, ($nodeTime->getTimestampMs() - $hq->getTimestampMs())));
        DB::table('node_heartbeats')->where('node_id', $node->id)->update([
            'app_version' => $body['app_version'] ?? null,
            'schema_version' => $body['schema_version'] ?? null,
            'master_version' => (int) $body['master_version'],
            'pending_outbox' => (int) $body['pending_outbox'],
            'failed_outbox' => (int) ($body['failed_outbox'] ?? 0),
            'node_time' => $nodeTime->format('Y-m-d H:i:s.v'),
            'clock_skew_ms' => $skew,
        ]);

        return response()->json([
            'hq_time' => $hq->format('Y-m-d H:i:s.v'),
            'clock_skew_ms' => $skew,
            'master_version' => (int) (DB::table('master_version')->where('id', 1)->value('last_version') ?? 0),
        ]);
    }

    private function node(Request $request): SyncNode
    {
        $node = $request->attributes->get('sync_node');
        abort_unless($node instanceof SyncNode, 401);

        return $node;
    }

    private function invalid(string $message): JsonResponse
    {
        return response()->json(['code' => 'invalid_request', 'message' => $message], 422);
    }
}
