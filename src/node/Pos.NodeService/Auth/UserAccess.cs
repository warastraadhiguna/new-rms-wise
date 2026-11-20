using Dapper;
using MySqlConnector;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.Shared.Auth;

namespace Pos.NodeService.Auth;

/// <summary>Queries shared by login and the per-request check.</summary>
public static class UserAccessQueries
{
    /// <summary>Role permissions of a user that apply at this node (roles for all nodes or for this node).</summary>
    public static async Task<IReadOnlyList<string?>> RolePermissionsAsync(
        MySqlConnection c, MySqlTransaction? tx, Guid userId, Guid nodeId, CancellationToken ct) =>
        (await c.QueryAsync<string?>(new CommandDefinition(
            """
            SELECT CAST(r.permissions AS CHAR)
            FROM user_roles ur JOIN roles r ON r.id = ur.role_id
            WHERE ur.user_id = @userId AND ur.deleted_at IS NULL AND r.deleted_at IS NULL
              AND (ur.node_id IS NULL OR ur.node_id = @nodeId)
            """, new { userId, nodeId }, tx, cancellationToken: ct))).ToList();
}

/// <summary>
/// Current state of a user at this node, re-read for every authenticated request (cached at most
/// 30 seconds, setting <c>Auth:UserCacheSeconds</c>). Null = the user is inactive or deleted.
/// </summary>
public sealed class UserAccessCache(DbConnectionFactory connections, NodeIdentity nodeIdentity, TimeProvider clock, TimeSpan ttl)
{
    public static readonly TimeSpan MaxTtl = TimeSpan.FromSeconds(30);

    private readonly ExpiringCache<Guid, PermissionSet?> _cache = new(ttl, clock);

    /// <summary>Default 30 s; never more than 30 s; negative means no cache.</summary>
    public static TimeSpan TtlFrom(int? configuredSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(configuredSeconds ?? (int)MaxTtl.TotalSeconds, 0, (int)MaxTtl.TotalSeconds));

    /// <returns>The user's current permissions at this node, or null when the user may no longer sign in.</returns>
    public Task<PermissionSet?> GetAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _cache.GetOrLoadAsync(userId, async id =>
        {
            var node = await nodeIdentity.GetAsync(cancellationToken);
            await using var connection = await connections.OpenAsync(cancellationToken);

            var active = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM users WHERE id = @id AND is_active = 1 AND deleted_at IS NULL",
                new { id }, cancellationToken: cancellationToken)) > 0;
            if (!active)
                return null;

            return PermissionSet.FromRoleJson(
                await UserAccessQueries.RolePermissionsAsync(connection, null, id, node.Id, cancellationToken));
        });
}

/// <summary>Raised when a valid token belongs to a user who is now inactive or deleted.</summary>
public sealed class UserInactiveException() : Exception("This user is no longer active.");
