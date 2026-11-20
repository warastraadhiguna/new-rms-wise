using System.Security.Claims;
using Pos.NodeService.Auth;
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

        // The user comes from the token and the device from X-Device-Id, never from the request body.
        var shifts = api.MapGroup("/shifts").AddEndpointFilter<DeviceFilter>();
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
                var result = await service.CreateAsync(request, DeviceFilter.DeviceId(http), user.UserId(),
                    canUseAnyShift: user.HasPermission(Permissions.ShiftShare), ct);
                return result.Replayed ? Results.Ok(result) : Results.Created($"/api/v1/sales/{result.Sale.Id}", result);
            })
            .AddEndpointFilter<DeviceFilter>()
            .RequireAuthorization(Permissions.SaleCreate);

        return app;
    }
}
