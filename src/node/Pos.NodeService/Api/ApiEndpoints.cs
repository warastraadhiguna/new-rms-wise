using System.Security.Claims;
using Pos.NodeService.Auth;
using Pos.NodeService.Catalog;
using Pos.NodeService.Customers;
using Pos.NodeService.Sales;
using Pos.NodeService.Shifts;
using Pos.Shared.Auth;

namespace Pos.NodeService.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiV1(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter<ApiExceptionFilter>();

        var auth = api.MapGroup("/auth");
        auth.MapPost("/login", (LoginRequest request, AuthService service, CancellationToken ct) =>
            service.LoginWithPasswordAsync(request, ct));
        auth.MapPost("/pin", (PinLoginRequest request, AuthService service, CancellationToken ct) =>
            service.LoginWithPinAsync(request, ct));
        // A supervisor approves one action for the logged-in user (one-time token, valid briefly).
        auth.MapPost("/approve", (ApproveRequest request, AuthService service, ClaimsPrincipal user, CancellationToken ct) =>
                service.ApproveAsync(request, user.UserId(), ct))
            .RequireAuthorization();

        // The user comes from the token and the device from X-Device-Id, never from the request body.
        var shifts = api.MapGroup("/shifts").AddEndpointFilter<DeviceFilter>();
        shifts.MapGet("/current", (ShiftService service, HttpContext http, CancellationToken ct) =>
                service.GetCurrentAsync(DeviceFilter.DeviceId(http), ct))
            .RequireAuthorization();
        shifts.MapPost("/open", async (OpenShiftRequest request, ShiftService service, HttpContext http, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var shift = await service.OpenAsync(request, DeviceFilter.DeviceId(http), user.UserId(), ct);
                return Results.Created($"/api/v1/shifts/{shift.Id}", shift);
            })
            .RequireAuthorization(Permissions.ShiftOpen);
        shifts.MapPost("/close", async (CloseShiftRequest request, ShiftService service, CancellationToken ct) =>
                Results.Ok(await service.CloseAsync(request, ct)))
            .RequireAuthorization(Permissions.ShiftClose);

        // 201 when created, 200 when the same sale id was already stored (idempotent retry from the client).
        api.MapPost("/sales", async (CreateSaleRequest request, SaleService service, HttpContext http, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var permissions = PermissionSet.FromNames(user.FindAll(TokenService.PermissionClaim).Select(c => c.Value));
                var result = await service.CreateAsync(request, DeviceFilter.DeviceId(http), user.UserId(), permissions, ct);
                return result.Replayed ? Results.Ok(result) : Results.Created($"/api/v1/sales/{result.Sale.Id}", result);
            })
            .AddEndpointFilter<DeviceFilter>()
            .RequireAuthorization(Permissions.SaleCreate);

        // F12 "Batal": a cancelled cart with its reason (201 created, 200 when the id was already stored).
        api.MapPost("/cart-cancellations", async (CartCancellationRequest request, CartCancellationService service, HttpContext http, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var permissions = PermissionSet.FromNames(user.FindAll(TokenService.PermissionClaim).Select(c => c.Value));
                var result = await service.CreateAsync(request, DeviceFilter.DeviceId(http), user.UserId(), permissions, ct);
                return result.Replayed
                    ? Results.Ok(result.Cancellation)
                    : Results.Created($"/api/v1/cart-cancellations/{result.Cancellation.Id}", result.Cancellation);
            })
            .AddEndpointFilter<DeviceFilter>()
            .RequireAuthorization(Permissions.SaleCreate);

        // Payment dialog: methods, cash rounding, currency precision, the user's discount limit.
        api.MapGet("/sales/config", (SalesConfigService service, ClaimsPrincipal user, CancellationToken ct) =>
                service.GetAsync(PermissionSet.FromNames(user.FindAll(TokenService.PermissionClaim).Select(c => c.Value)), ct))
            .RequireAuthorization(Permissions.SaleCreate);

        api.MapGet("/products/search", (string? q, ProductSearchService service, CancellationToken ct) => service.SearchAsync(q, ct))
            .RequireAuthorization(Permissions.SaleCreate);
        api.MapGet("/products/{productId:guid}/units", (Guid productId, ProductSearchService service, CancellationToken ct) =>
                service.GetUnitsAsync(productId, ct))
            .RequireAuthorization(Permissions.SaleCreate);
        api.MapGet("/customers/search", (string? q, CustomerSearchService service, CancellationToken ct) => service.SearchAsync(q, ct))
            .RequireAuthorization(Permissions.SaleCreate);

        return app;
    }
}
