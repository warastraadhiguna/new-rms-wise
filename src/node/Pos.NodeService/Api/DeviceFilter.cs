using Dapper;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;

namespace Pos.NodeService.Api;

/// <summary>
/// Requires header <c>X-Device-Id</c> naming an ACTIVE device of this node (temporary until device
/// activation with keys exists). The device id is then available through <see cref="DeviceId"/>.
/// </summary>
public sealed class DeviceFilter(DbConnectionFactory connections, NodeIdentity nodeIdentity) : IEndpointFilter
{
    public const string Header = "X-Device-Id";
    private const string ItemKey = "pos.device_id";

    public static Guid DeviceId(HttpContext context) =>
        context.Items[ItemKey] as Guid? ?? throw new InvalidOperationException("DeviceFilter did not run for this endpoint.");

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.Headers[Header].ToString(), out var deviceId))
            throw new ApiException(StatusCodes.Status400BadRequest, "device_required", $"Header {Header} with the device id is required.");

        var node = await nodeIdentity.GetAsync(http.RequestAborted);
        await using var connection = await connections.OpenAsync(http.RequestAborted);
        var allowed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM devices WHERE id = @deviceId AND node_id = @nodeId AND status = 'ACTIVE' AND deleted_at IS NULL",
            new { deviceId, nodeId = node.Id }, cancellationToken: http.RequestAborted)) > 0;
        if (!allowed)
            throw new ApiException(StatusCodes.Status403Forbidden, "device_not_allowed",
                $"Device {deviceId} is not an active device of node {node.Code}.");

        http.Items[ItemKey] = deviceId;
        return await next(context);
    }
}
