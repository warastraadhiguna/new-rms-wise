using Dapper;
using Pos.NodeService.Data;

namespace Pos.NodeService.Nodes;

/// <summary>This Node Service's own node, its branch and the branch timezone.</summary>
public sealed record NodeInfo(Guid Id, string Code, Guid BranchId, string TimeZone);

public sealed class NodeNotFoundException(string code)
    : Exception($"Node '{code}' (setting Node:Code) does not exist in the database yet. Run the dev seed or sync master data from HQ.");

/// <summary>
/// Resolves the node configured in <c>Node:Code</c> (e.g. TK01). The row arrives with master data,
/// so it may be missing right after installation; it is cached once found.
/// </summary>
public sealed class NodeIdentity(DbConnectionFactory connections, string code)
{
    private NodeInfo? _cached;

    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("Node code is required (setting Node:Code).", nameof(code))
        : code.Trim().ToUpperInvariant();

    public async Task<NodeInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
            return _cached;

        await using var connection = await connections.OpenAsync(cancellationToken);
        var node = await connection.QuerySingleOrDefaultAsync<NodeInfo>(new CommandDefinition(
            """
            SELECT n.id AS Id, n.code AS Code, n.branch_id AS BranchId, b.timezone AS TimeZone
            FROM nodes n JOIN branches b ON b.id = n.branch_id
            WHERE n.code = @Code AND n.deleted_at IS NULL
            """,
            new { Code }, cancellationToken: cancellationToken));

        return _cached = node ?? throw new NodeNotFoundException(Code);
    }
}
