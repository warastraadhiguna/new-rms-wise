using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>
/// A stand-in for the HQ sync API on a real local port, speaking the JSON contract of docs/sync-contract.md (bearer
/// token, gzip request bodies, gzip responses, pull cursor with safe_version, push results per document). Tests add
/// master changes, read what the node pushed, and switch failures on: <see cref="Offline"/> (every request dropped) or
/// <see cref="DropPushAfterDocuments"/> (the connection breaks in the middle of a batch, after HQ stored some).
/// </summary>
public sealed class FakeHq : IAsyncDisposable
{
    public const string Token = "pos_FAKE_HQ_TOKEN_0123456789abcdefghijklmnop";

    private readonly WebApplication _app;
    private readonly Lock _gate = new();
    private readonly List<(string Table, long Version, JsonObject Row)> _changes = [];
    private long _version;

    /// <summary>Documents received, by doc_id: the highest row_version kept (like inbound_documents).</summary>
    public ConcurrentDictionary<string, JsonObject> Documents { get; } = new();

    /// <summary>Every result HQ answered, in order ("accepted", "duplicate", "stale", "rejected").</summary>
    public ConcurrentQueue<(string DocId, string Status)> Answers { get; } = new();

    public ConcurrentQueue<JsonObject> Heartbeats { get; } = new();

    /// <summary>doc_id → (code, retry): answer "rejected" for these documents.</summary>
    public ConcurrentDictionary<string, (string Code, bool Retry)> Reject { get; } = new();

    public volatile bool Offline;

    /// <summary>Next push only: store this many documents, then cut the connection (the node never gets an answer).</summary>
    public int? DropPushAfterDocuments;

    public int PushRequests;

    public string Url { get; private set; } = "";

    private FakeHq(WebApplication app) => _app = app;

    /// <param name="startVersion">HQ versions continue after this one (what the node already has).</param>
    public static async Task<FakeHq> StartAsync(long startVersion = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var hq = new FakeHq(app) { _version = startVersion };

        var sync = app.MapGroup("/api/v1/sync");
        sync.MapGet("/pull", async (HttpContext http, CancellationToken _) => await hq.HandleAsync(http, () => Task.FromResult(hq.Pull(http))));
        sync.MapPost("/push", async (HttpContext http, CancellationToken _) => await hq.HandleAsync(http, async () => await hq.PushAsync(http)));
        sync.MapPost("/heartbeat", async (HttpContext http, CancellationToken _) => await hq.HandleAsync(http, async () =>
        {
            var body = await ReadAsync(http);
            hq.Heartbeats.Enqueue(body);
            return Results.Json(new JsonObject { ["hq_time"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"), ["clock_skew_ms"] = 0, ["master_version"] = hq._version });
        }));

        await app.StartAsync();
        hq.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return hq;
    }

    /// <summary>A master row as HQ would send it; gets the next version (like MasterDataWriter).</summary>
    public long AddChange(string table, JsonObject row)
    {
        lock (_gate)
        {
            var version = ++_version;
            row["version"] = version;
            _changes.RemoveAll(c => c.Table == table && c.Row["id"]!.GetValue<string>() == row["id"]!.GetValue<string>());
            _changes.Add((table, version, row));
            return version;
        }
    }

    public IEnumerable<JsonObject> DocumentsOfType(string docType) => Documents.Values.Where(d => d["doc_type"]!.GetValue<string>() == docType);

    public JsonObject? Row(string docId, string table) =>
        Documents.TryGetValue(docId, out var doc)
            ? doc["payload"]!["tables"]!.AsArray().FirstOrDefault(t => t!["table"]!.GetValue<string>() == table)?["rows"]!.AsArray()[0]!.AsObject()
            : null;

    private async Task<IResult> HandleAsync(HttpContext http, Func<Task<IResult>> action)
    {
        if (Offline)
        {
            http.Abort();
            return Results.Empty;
        }
        if (http.Request.Headers.Authorization.ToString() != "Bearer " + Token)
            return Results.Json(new JsonObject { ["code"] = "invalid_token", ["message"] = "Token sinkron tidak valid." }, statusCode: 401);
        return await action();
    }

    private IResult Pull(HttpContext http)
    {
        var since = long.Parse(http.Request.Query["since"].ToString() is { Length: > 0 } s ? s : "0");
        var limit = int.Parse(http.Request.Query["limit"].ToString() is { Length: > 0 } l ? l : "500");
        List<(string Table, long Version, JsonObject Row)> eligible;
        long safe;
        lock (_gate)
        {
            safe = _version;
            eligible = _changes.Where(c => c.Version > since && c.Version <= safe).OrderBy(c => c.Version).ToList();
        }
        var page = eligible.Take(limit).ToList();
        var hasMore = eligible.Count > limit;
        var body = new JsonObject
        {
            ["since"] = since,
            ["next_since"] = hasMore ? page[^1].Version : Math.Max(since, safe),
            ["safe_version"] = safe,
            ["has_more"] = hasMore,
            ["changes"] = new JsonArray(page.Select(c => (JsonNode)new JsonObject
            {
                ["table"] = c.Table, ["version"] = c.Version, ["row"] = c.Row.DeepClone(),
            }).ToArray()),
        };
        return Results.Json(body);
    }

    private async Task<IResult> PushAsync(HttpContext http)
    {
        Interlocked.Increment(ref PushRequests);
        var documents = (await ReadAsync(http))["documents"]!.AsArray();
        var drop = DropPushAfterDocuments;
        DropPushAfterDocuments = null;

        var results = new JsonArray();
        var stored = 0;
        foreach (var doc in documents.Select(d => d!.AsObject()))
        {
            if (drop is { } max && stored >= max)
            {
                http.Abort();                                   // the internet goes away in the middle of the batch
                return Results.Empty;
            }
            var docId = doc["doc_id"]!.GetValue<string>();
            var version = doc["row_version"]!.GetValue<int>();
            string status;
            if (Reject.TryGetValue(docId, out var rejection))
            {
                Answers.Enqueue((docId, "rejected"));
                results.Add(new JsonObject
                {
                    ["outbox_id"] = doc["outbox_id"]!.GetValue<long>(), ["doc_id"] = docId, ["status"] = "rejected",
                    ["code"] = rejection.Code, ["message"] = "Ditolak oleh test.", ["retry"] = rejection.Retry,
                });
                continue;
            }
            if (Documents.TryGetValue(docId, out var existing))
            {
                var known = existing["row_version"]!.GetValue<int>();
                status = known == version ? "duplicate" : known > version ? "stale" : "accepted";
            }
            else
            {
                status = "accepted";
            }
            if (status == "accepted")
                Documents[docId] = (JsonObject)doc.DeepClone();
            stored++;
            Answers.Enqueue((docId, status));
            results.Add(new JsonObject { ["outbox_id"] = doc["outbox_id"]!.GetValue<long>(), ["doc_id"] = docId, ["status"] = status });
        }
        return Results.Json(new JsonObject { ["results"] = results });
    }

    private static async Task<JsonObject> ReadAsync(HttpContext http)
    {
        Stream body = http.Request.Body;
        if (http.Request.Headers.ContentEncoding.ToString() == "gzip")
            body = new GZipStream(body, CompressionMode.Decompress);
        using var reader = new StreamReader(body);
        return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
