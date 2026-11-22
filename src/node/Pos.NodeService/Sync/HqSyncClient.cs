using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Pos.NodeService.Sync;

/// <summary>No answer from HQ (no internet, timeout, HQ down or busy): try again later, nothing was decided.</summary>
public sealed class SyncUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>HQ refused the node itself (token revoked or wrong, node inactive) or the request: needs an admin.</summary>
public sealed class SyncRefusedException(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

public sealed record PullChange(string Table, long Version, JsonObject Row);

public sealed record PullPage(long Since, long NextSince, long SafeVersion, bool HasMore, IReadOnlyList<PullChange> Changes);

/// <param name="Status">accepted | duplicate | stale | rejected</param>
public sealed record PushResult(long OutboxId, string Status, string? Code, string? Message, bool Retry);

public sealed record HeartbeatReport(string AppVersion, string? SchemaVersion, long MasterVersion, int PendingOutbox, int FailedOutbox, DateTime NodeTimeUtc);

public sealed record HeartbeatReply(long ClockSkewMs, long HqMasterVersion);

/// <summary>The HQ sync API (docs/sync-contract.md): bearer token, gzip request bodies, gzip responses.</summary>
public sealed class HqSyncClient(HttpClient http, SyncOptions options, SyncTokenStore tokens)
{
    public const string HttpClientName = "hq-sync";

    public async Task<PullPage> PullAsync(long since, int limit, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"pull?since={since}&limit={limit}", null, ct);
        var changes = json["changes"]!.AsArray().Select(c => new PullChange(
            c!["table"]!.GetValue<string>(), c["version"]!.GetValue<long>(), c["row"]!.AsObject())).ToList();
        return new PullPage(json["since"]!.GetValue<long>(), json["next_since"]!.GetValue<long>(),
            json["safe_version"]!.GetValue<long>(), json["has_more"]!.GetValue<bool>(), changes);
    }

    public async Task<IReadOnlyList<PushResult>> PushAsync(JsonArray documents, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "push", new JsonObject { ["documents"] = documents }, ct);
        return json["results"]!.AsArray().Select(r => new PushResult(
            r!["outbox_id"]?.GetValue<long>() ?? 0,
            r["status"]!.GetValue<string>(),
            r["code"]?.GetValue<string>(),
            r["message"]?.GetValue<string>(),
            r["retry"]?.GetValue<bool>() ?? false)).ToList();
    }

    public async Task<HeartbeatReply> HeartbeatAsync(HeartbeatReport report, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "heartbeat", new JsonObject
        {
            ["app_version"] = report.AppVersion,
            ["schema_version"] = report.SchemaVersion,
            ["master_version"] = report.MasterVersion,
            ["pending_outbox"] = report.PendingOutbox,
            ["failed_outbox"] = report.FailedOutbox,
            ["node_time"] = report.NodeTimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
        }, ct);
        return new HeartbeatReply(json["clock_skew_ms"]?.GetValue<long>() ?? 0, json["master_version"]?.GetValue<long>() ?? 0);
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        if (!options.HasHq)
            throw new SyncRefusedException(0, "not_configured", "Sync:HqUrl is not configured.");
        var token = tokens.Load()
            ?? throw new SyncRefusedException(0, "no_token", "No sync token: run Pos.NodeService set-sync-token <token>.");

        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.HqUrl!.TrimEnd('/') + "/"), "api/v1/sync/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        if (body is not null)
        {
            using var buffer = new MemoryStream();
            await using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), ct);
            var content = new ByteArrayContent(buffer.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            request.Content = content;
        }

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds)));
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new SyncUnavailableException($"HQ tidak terjangkau: {ex.Message}", ex);
        }

        using (response)
        {
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new SyncUnavailableException($"Jawaban HQ terputus: {ex.Message}", ex);
            }

            if (response.IsSuccessStatusCode)
            {
                return JsonNode.Parse(text) as JsonObject
                    ?? throw new SyncUnavailableException("Jawaban HQ bukan JSON.");
            }

            var status = (int)response.StatusCode;
            if (status >= 500 || status == 429 || status == 408)
                throw new SyncUnavailableException($"HQ menjawab {status}.");

            var error = TryParse(text);
            throw new SyncRefusedException(response.StatusCode, error?["code"]?.GetValue<string>() ?? $"http_{status}",
                error?["message"]?.GetValue<string>() ?? $"HQ menolak request ({status}).");
        }
    }

    private static JsonObject? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
