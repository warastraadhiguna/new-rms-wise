using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Pos.NodeService.Auth;
using Pos.NodeService.Catalog;
using Pos.NodeService.Customers;
using Pos.NodeService.Sales;
using Pos.NodeService.Shifts;
using Pos.NodeService.Nodes;
using Pos.NodeService.Stock;
using Pos.Shared.Time;
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

        // Receipt data of a stored sale (print after payment, F5 reprint): names, cashier, branch timezone, receipt texts.
        api.MapGet("/sales/{saleId:guid}", (Guid saleId, SaleDetailService service, CancellationToken ct) => service.GetAsync(saleId, ct))
            .RequireAuthorization(Permissions.SaleCreate);

        // Product lookup is shared by the cashier, the stock screens and Form Mutasi Lokasi.
        api.MapGet("/products/search", (string? q, ProductSearchService service, CancellationToken ct) => service.SearchAsync(q, ct))
            .RequireAuthorization(Policies.ProductLookup);
        api.MapGet("/products/{productId:guid}/units", (Guid productId, ProductSearchService service, CancellationToken ct) =>
                service.GetUnitsAsync(productId, ct))
            .RequireAuthorization(Policies.ProductLookup);
        api.MapGet("/customers/search", (string? q, CustomerSearchService service, CancellationToken ct) => service.SearchAsync(q, ct))
            .RequireAuthorization(Permissions.SaleCreate);

        // Store/warehouse admin screens (read-only). No purchase prices or cost at a node: HPP is computed at HQ.
        api.MapGet("/stock/locations", (StockQueryService service, CancellationToken ct) => service.GetLocationsAsync(ct))
            .RequireAuthorization(Policies.StockTransferRead);
        var stock = api.MapGroup("/stock").RequireAuthorization(Permissions.StockView);
        stock.MapGet("/totals", (string? q, [FromQuery(Name = "location_id")] Guid? locationId, int? offset, int? limit,
                [FromQuery(Name = "include_empty")] bool? includeEmpty,
                StockQueryService service, CancellationToken ct) =>
            service.GetTotalsAsync(q, locationId, offset ?? 0, limit ?? 200, ct, includeEmpty ?? false));
        stock.MapGet("/products/{productId:guid}/expiry", (Guid productId, [FromQuery(Name = "location_id")] Guid? locationId,
                StockQueryService service, CancellationToken ct) =>
            service.GetExpiryAsync(productId, locationId, ct));
        // Kartu Stok: paged ledger rows with running balance; from/to are branch dates (default: this month).
        stock.MapGet("/card", async ([FromQuery(Name = "product_id")] Guid productId, [FromQuery(Name = "location_id")] Guid? locationId,
                DateOnly? from, DateOnly? to, int? offset, int? limit, StockCardService service, NodeIdentity nodeIdentity,
                TimeProvider clock, CancellationToken ct) =>
            {
                var today = BranchClock.Today(clock.GetUtcNow().UtcDateTime, (await nodeIdentity.GetAsync(ct)).TimeZone);
                return await service.GetAsync(productId, locationId, from ?? new DateOnly(today.Year, today.Month, 1), to ?? today,
                    offset ?? 0, limit ?? 500, ct);
            });

        // Form Mutasi Lokasi (stock.transfer): 201 created, 200 when the same id was already stored (client retry).
        var transfers = api.MapGroup("/stock/transfers");
        transfers.MapPost("", async (CreateLocationTransferRequest request, LocationTransferService service, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var result = await service.CreateAsync(request, user.UserId(), ct);
                return result.Replayed
                    ? Results.Ok(result.Transfer)
                    : Results.Created($"/api/v1/stock/transfers/{result.Transfer.Id}", result.Transfer);
            })
            .RequireAuthorization(Permissions.StockTransfer);
        transfers.MapPost("/preview", (LocationTransferPreviewRequest request, LocationTransferService service, CancellationToken ct) =>
                service.PreviewAsync(request, ct))
            .RequireAuthorization(Permissions.StockTransfer);
        // Daftar Mutasi Lokasi and the detail (also from the Kartu Stok): branch dates, default this month.
        transfers.MapGet("", async (DateOnly? from, DateOnly? to, LocationTransferService service, NodeIdentity nodeIdentity,
                TimeProvider clock, CancellationToken ct) =>
            {
                var today = BranchClock.Today(clock.GetUtcNow().UtcDateTime, (await nodeIdentity.GetAsync(ct)).TimeZone);
                return await service.ListAsync(from ?? new DateOnly(today.Year, today.Month, 1), to ?? today, ct);
            })
            .RequireAuthorization(Policies.StockTransferRead);
        transfers.MapGet("/{transferId:guid}", (Guid transferId, LocationTransferService service, CancellationToken ct) =>
                service.GetAsync(transferId, ct))
            .RequireAuthorization(Policies.StockTransferRead);

        // Stock integrity (admin). The nightly check never repairs; a person reviews and then rebuilds.
        var stockAdmin = api.MapGroup("/admin/stock").RequireAuthorization(Permissions.StockRebuild);
        stockAdmin.MapPost("/integrity-check", (StockIntegrityService service, CancellationToken ct) => service.CheckAsync(ct));
        stockAdmin.MapPost("/rebuild-balances", (StockIntegrityService service, ClaimsPrincipal user, CancellationToken ct) =>
            service.RebuildAsync(user.UserId(), user.FindFirstValue(TokenService.NameClaim), ct));

        return app;
    }
}
