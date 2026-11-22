<?php

namespace App\Http\Middleware;

use App\Sync\NodeTokens;
use Closure;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Symfony\Component\HttpFoundation\Response;

/**
 * Sync API: the bearer token decides the node; every request acts as that node (request attribute "sync_node").
 * Marks the token as used and the node as seen (node_heartbeats.last_seen_at) on every request.
 */
class AuthenticateNode
{
    public function __construct(private readonly NodeTokens $tokens) {}

    public function handle(Request $request, Closure $next): Response
    {
        $resolved = $this->tokens->resolve((string) $request->bearerToken());
        if ($resolved === null) {
            return response()->json(['code' => 'invalid_token', 'message' => 'Token sinkron tidak valid atau sudah dicabut.'], 401);
        }
        if (! $resolved['active']) {
            return response()->json(['code' => 'node_inactive', 'message' => 'Node ini nonaktif.'], 403);
        }

        $node = $resolved['node'];
        $request->attributes->set('sync_node', $node);

        $now = NodeTokens::now();
        DB::table('node_sync_tokens')->where('id', $node->tokenId)->update(['last_used_at' => $now]);
        DB::statement(
            'INSERT INTO node_heartbeats (node_id, last_seen_at, last_ip) VALUES (?, ?, ?)
             ON DUPLICATE KEY UPDATE last_seen_at = VALUES(last_seen_at), last_ip = VALUES(last_ip)',
            [$node->id, $now, $request->ip()],
        );

        return $next($request);
    }
}
