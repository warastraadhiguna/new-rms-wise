using Pos.NodeService.Nodes;
using Pos.Shared.Sales;
using Pos.Shared.Units;

namespace Pos.NodeService.Api;

/// <summary>
/// A business rule rejected the request. Rendered as RFC 7807 problem details with a stable
/// machine-readable <c>code</c> (snake_case) plus optional extra fields.
/// </summary>
public sealed class ApiException(int status, string code, string message, IReadOnlyDictionary<string, object?>? extra = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object?> Extra { get; } = extra ?? new Dictionary<string, object?>();

    public static ApiException Unprocessable(string code, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(StatusCodes.Status422UnprocessableEntity, code, message, extra);

    public static ApiException Conflict(string code, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(StatusCodes.Status409Conflict, code, message, extra);

    public static ApiException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);
}

/// <summary>Maps known exceptions of /api endpoints to problem responses.</summary>
public sealed class ApiExceptionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (ApiException ex)
        {
            return Problem(ex.Status, ex.Code, ex.Message, ex.Extra);
        }
        catch (SaleValidationException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, ex.Code, ex.Message);
        }
        catch (InvalidQuantityException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "invalid_quantity", ex.Message);
        }
        catch (NodeNotFoundException ex)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "node_not_configured", ex.Message);
        }
    }

    private static IResult Problem(int status, string code, string detail, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var extensions = new Dictionary<string, object?>(extra ?? new Dictionary<string, object?>()) { ["code"] = code };
        return Results.Problem(detail: detail, statusCode: status, title: code, extensions: extensions);
    }
}
