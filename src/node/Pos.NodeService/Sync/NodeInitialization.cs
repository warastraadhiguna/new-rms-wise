using Dapper;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;

namespace Pos.NodeService.Sync;

/// <summary>
/// A node sells only after its master data is complete: <c>init-from-hq</c> (production) or the dev seed set
/// sync_state <c>node.initialized</c>. Until then every document endpoint answers 503 <c>node_not_initialized</c>.
/// </summary>
public sealed class NodeInitialization(DbConnectionFactory connections)
{
    private volatile bool _initialized;

    public async Task<bool> IsInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return true;
        await using var c = await connections.OpenAsync(ct);
        return _initialized = await SyncState.GetAsync(c, null, SyncState.Initialized, ct) is not null;
    }

    public void Reset() => _initialized = false;
}

/// <summary>Endpoint filter for everything that creates a document (shift, sale, cancellation, void, return, transfer).</summary>
public sealed class NodeInitializedFilter(NodeInitialization initialization) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await initialization.IsInitializedAsync(context.HttpContext.RequestAborted))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "node_not_initialized",
                "Node belum diinisialisasi dari pusat (init-from-hq); belum bisa berjualan.");
        return await next(context);
    }
}

public sealed record InitResult(bool Success, string Message, int Rows = 0, long MasterVersion = 0);

/// <summary>
/// <c>Pos.NodeService init-from-hq</c>: fills an EMPTY node database (just migrated) with all master data from HQ,
/// page by page, and marks the node initialized. Refuses a database that already holds master data (e.g. an old local
/// dev seed: its ids differ from HQ's, so the two can never be merged — see docs/node-setup.md).
/// </summary>
public sealed class NodeInitializer(DbConnectionFactory connections, SyncAgent agent, NodeIdentity nodeIdentity, NodeInitialization initialization)
{
    private static readonly string[] MasterTables = ["nodes", "branches", "users", "products", "units", "payment_methods"];

    public async Task<InitResult> RunAsync(CancellationToken ct)
    {
        if (!agent.Configured)
            return new InitResult(false, "Sync:HqUrl is not configured (appsettings.Local.json).");

        await using (var c = await connections.OpenAsync(ct))
        {
            if (await SyncState.GetAsync(c, null, SyncState.Initialized, ct) is { } done)
                return new InitResult(false, $"This node is already initialized ({done}).");
            foreach (var table in MasterTables)
            {
                if (await c.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM `{table}`", cancellationToken: ct)) > 0)
                    return new InitResult(false,
                        $"The database is not empty (table {table} has rows). init-from-hq needs a new, empty database; see docs/node-setup.md.");
            }
        }

        var rows = await agent.PullAllAsync(ct);
        nodeIdentity.Reset();
        NodeInfo node;
        try
        {
            node = await nodeIdentity.GetAsync(ct);
        }
        catch (NodeNotFoundException)
        {
            return new InitResult(false, $"HQ has no node {nodeIdentity.Code} (setting Node:Code), or the token belongs to another node. {rows} rows were stored; the node is NOT initialized.", rows);
        }

        await using (var c = await connections.OpenAsync(ct))
            await SyncState.SetAsync(c, null, SyncState.Initialized, "HQ", ct);
        initialization.Reset();
        var version = await agent.MasterVersionAsync(ct);
        return new InitResult(true, $"Node {node.Code} initialized from HQ: {rows} master rows, version {version}.", rows, version);
    }
}
