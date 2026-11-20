using Pos.NodeService.Sales;
using Pos.NodeService.Shifts;

namespace Pos.NodeService.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiV1(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter<ApiExceptionFilter>();

        var shifts = api.MapGroup("/shifts");
        shifts.MapPost("/open", async (OpenShiftRequest request, ShiftService service, CancellationToken ct) =>
        {
            var shift = await service.OpenAsync(request, ct);
            return Results.Created($"/api/v1/shifts/{shift.Id}", shift);
        });
        shifts.MapPost("/close", async (CloseShiftRequest request, ShiftService service, CancellationToken ct) =>
            Results.Ok(await service.CloseAsync(request, ct)));

        // 201 when created, 200 when the same sale id was already stored (idempotent retry from the client).
        api.MapPost("/sales", async (CreateSaleRequest request, SaleService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.Replayed ? Results.Ok(result) : Results.Created($"/api/v1/sales/{result.Sale.Id}", result);
        });

        return app;
    }
}
