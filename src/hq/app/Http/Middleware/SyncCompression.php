<?php

namespace App\Http\Middleware;

use Closure;
use Illuminate\Http\Request;
use Symfony\Component\HttpFoundation\Response;

/**
 * Sync API bodies: requests may come gzip-compressed (Content-Encoding: gzip); responses are compressed when the
 * client accepts gzip. The uncompressed body is limited (pos.sync.max_body_bytes, 413 batch_too_large).
 */
class SyncCompression
{
    /** Request attribute with the uncompressed body (read it with SyncCompression::json()). */
    public const BODY = 'sync_body';

    /** The request body as JSON, uncompressed when it came gzipped. */
    public static function json(Request $request): mixed
    {
        $body = $request->attributes->get(self::BODY);

        return is_string($body) ? json_decode($body, true) : $request->json()->all();
    }

    public function handle(Request $request, Closure $next): Response
    {
        $max = (int) config('pos.sync.max_body_bytes');
        $raw = (string) $request->getContent();

        if (strtolower((string) $request->header('Content-Encoding')) === 'gzip') {
            $body = @gzdecode($raw, $max + 1);
            if ($body === false) {
                return strlen($raw) > 0 && $this->tooLarge($raw, $max)
                    ? $this->batchTooLarge()
                    : response()->json(['code' => 'invalid_request', 'message' => 'Body gzip tidak valid.'], 400);
            }
            if (strlen($body) > $max) {
                return $this->batchTooLarge();
            }
            $request->attributes->set(self::BODY, $body);
        } elseif (strlen($raw) > $max) {
            return $this->batchTooLarge();
        }

        $response = $next($request);

        $content = $response->getContent();
        if (is_string($content) && strlen($content) > 1024 && str_contains(strtolower((string) $request->header('Accept-Encoding')), 'gzip')) {
            $compressed = gzencode($content, 6);
            if ($compressed !== false) {
                $response->setContent($compressed);
                $response->headers->set('Content-Encoding', 'gzip');
                $response->headers->set('Content-Length', (string) strlen($compressed));
                $response->headers->set('Vary', 'Accept-Encoding');
            }
        }

        return $response;
    }

    /** gzdecode with a maximum length fails when the data is longer: check whether it is valid but too big. */
    private function tooLarge(string $raw, int $max): bool
    {
        $context = inflate_init(ZLIB_ENCODING_GZIP);
        if ($context === false) {
            return false;
        }
        $length = 0;
        foreach (str_split($raw, 65536) as $chunk) {
            $out = @inflate_add($context, $chunk);
            if ($out === false) {
                return false;
            }
            $length += strlen($out);
            if ($length > $max) {
                return true;
            }
        }

        return false;
    }

    private function batchTooLarge(): Response
    {
        return response()->json(['code' => 'batch_too_large', 'message' => 'Request terlalu besar.'], 413);
    }
}
