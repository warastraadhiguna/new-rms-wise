using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Desktop.Configuration;
using Pos.Shared.Sync;

namespace Pos.Desktop.Services;

/// <summary>
/// HTTP client for the store's Node Service. Uses the shared JSON conventions (snake_case, decimals as
/// strings), sends the login token and the device id, and turns every failure into a
/// <see cref="NodeApiException"/> with an Indonesian message.
/// </summary>
public sealed class NodeApiClient(HttpClient http, SessionState session, DesktopSettings settings) : INodeApi
{
    public const string DeviceHeader = "X-Device-Id";

    public Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default) =>
        SendAsync<LoginResult>(HttpMethod.Post, "api/v1/auth/login", new { Username = username, Password = password },
            authenticated: false, withDevice: false, ct);

    public async Task<ShiftInfo?> GetCurrentShiftAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<ShiftInfo>(HttpMethod.Get, "api/v1/shifts/current", null, authenticated: true, withDevice: true, ct);
        }
        catch (NodeApiException ex) when (ex.Code == "no_open_shift")
        {
            return null;
        }
    }

    public Task<ShiftSummary> GetShiftClosingAsync(Guid shiftId, CancellationToken ct = default) =>
        SendAsync<ShiftSummary>(HttpMethod.Get, $"api/v1/shifts/{shiftId}/closing", null, authenticated: true, withDevice: true, ct);

    public Task<ShiftSummary> CloseShiftAsync(CloseShiftRequest request, CancellationToken ct = default) =>
        SendAsync<ShiftSummary>(HttpMethod.Post, "api/v1/shifts/close", request, authenticated: true, withDevice: true, ct);

    public Task<ShiftInfo> OpenShiftAsync(decimal openingCash, CancellationToken ct = default) =>
        SendAsync<ShiftInfo>(HttpMethod.Post, "api/v1/shifts/open", new { OpeningCash = openingCash },
            authenticated: true, withDevice: true, ct);

    public async Task<IReadOnlyList<ProductItem>> SearchProductsAsync(string query, CancellationToken ct = default) =>
        await SendAsync<List<ProductItem>>(HttpMethod.Get, "api/v1/products/search?q=" + Uri.EscapeDataString(query), null,
            authenticated: true, withDevice: false, ct);

    public async Task<IReadOnlyList<ProductItem>> GetProductUnitsAsync(Guid productId, CancellationToken ct = default) =>
        await SendAsync<List<ProductItem>>(HttpMethod.Get, $"api/v1/products/{productId}/units", null,
            authenticated: true, withDevice: false, ct);

    public async Task<IReadOnlyList<CustomerItem>> SearchCustomersAsync(string query, CancellationToken ct = default) =>
        await SendAsync<List<CustomerItem>>(HttpMethod.Get, "api/v1/customers/search?q=" + Uri.EscapeDataString(query), null,
            authenticated: true, withDevice: false, ct);

    public Task<ApprovalResult> ApproveAsync(string action, string username, string password, CancellationToken ct = default) =>
        SendAsync<ApprovalResult>(HttpMethod.Post, "api/v1/auth/approve", new { Action = action, Username = username, Password = password },
            authenticated: true, withDevice: false, ct);

    public Task<SalesConfig> GetSalesConfigAsync(CancellationToken ct = default) =>
        SendAsync<SalesConfig>(HttpMethod.Get, "api/v1/sales/config", null, authenticated: true, withDevice: false, ct);

    public Task<SyncStatusInfo> GetSyncStatusAsync(CancellationToken ct = default) =>
        SendAsync<SyncStatusInfo>(HttpMethod.Get, "api/v1/sync/status", null, authenticated: true, withDevice: false, ct);

    public Task<SaleResult> CreateSaleAsync(CreateSaleRequest request, CancellationToken ct = default) =>
        SendAsync<SaleResult>(HttpMethod.Post, "api/v1/sales", request, authenticated: true, withDevice: true, ct);

    public Task<SaleDetail> GetSaleAsync(Guid saleId, CancellationToken ct = default) =>
        SendAsync<SaleDetail>(HttpMethod.Get, $"api/v1/sales/{saleId}", null, authenticated: true, withDevice: false, ct);

    public Task<SalesListPage> GetSalesAsync(DateOnly from, DateOnly to, string? q, int offset, int limit, CancellationToken ct = default) =>
        SendAsync<SalesListPage>(HttpMethod.Get,
            $"api/v1/sales?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Uri.EscapeDataString(q ?? "")}&offset={offset}&limit={limit}",
            null, authenticated: true, withDevice: false, ct);

    public async Task<IReadOnlyList<ReasonItem>> GetReasonsAsync(string type, CancellationToken ct = default) =>
        await SendAsync<List<ReasonItem>>(HttpMethod.Get, "api/v1/reasons?type=" + Uri.EscapeDataString(type), null,
            authenticated: true, withDevice: false, ct);

    public Task<SaleVoidInfo> VoidSaleAsync(Guid saleId, VoidSaleRequest request, CancellationToken ct = default) =>
        SendAsync<SaleVoidInfo>(HttpMethod.Post, $"api/v1/sales/{saleId}/void", request, authenticated: true, withDevice: false, ct);

    public Task<ReturnSource> GetReturnSourceAsync(Guid? saleId, string? number, CancellationToken ct = default) =>
        SendAsync<ReturnSource>(HttpMethod.Get,
            saleId is { } id ? $"api/v1/returns/source?sale_id={id}" : "api/v1/returns/source?number=" + Uri.EscapeDataString(number ?? ""),
            null, authenticated: true, withDevice: false, ct);

    /// <summary>With the device header: a cash refund comes out of this PC's open shift.</summary>
    public Task<SaleReturnInfo> CreateReturnAsync(CreateSaleReturnRequest request, CancellationToken ct = default) =>
        SendAsync<SaleReturnInfo>(HttpMethod.Post, "api/v1/returns", request, authenticated: true, withDevice: true, ct);

    public async Task<IReadOnlyList<SaleReturnSummary>> GetReturnsAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await SendAsync<List<SaleReturnSummary>>(HttpMethod.Get, $"api/v1/returns?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", null,
            authenticated: true, withDevice: false, ct);

    public Task<SaleReturnInfo> GetReturnAsync(Guid returnId, CancellationToken ct = default) =>
        SendAsync<SaleReturnInfo>(HttpMethod.Get, $"api/v1/returns/{returnId}", null, authenticated: true, withDevice: false, ct);

    public async Task<IReadOnlyList<StockLocation>> GetStockLocationsAsync(CancellationToken ct = default) =>
        await SendAsync<List<StockLocation>>(HttpMethod.Get, "api/v1/stock/locations", null, authenticated: true, withDevice: false, ct);

    public Task<StockTotalsPage> GetStockTotalsAsync(
        string? query, Guid? locationId, int offset, int limit, CancellationToken ct = default, bool includeEmpty = false) =>
        SendAsync<StockTotalsPage>(HttpMethod.Get,
            $"api/v1/stock/totals?q={Uri.EscapeDataString(query ?? "")}&offset={offset}&limit={limit}" +
            (locationId is { } location ? $"&location_id={location}" : "") + (includeEmpty ? "&include_empty=true" : ""),
            null, authenticated: true, withDevice: false, ct);

    public Task<StockExpiry> GetStockExpiryAsync(Guid productId, Guid? locationId, CancellationToken ct = default) =>
        SendAsync<StockExpiry>(HttpMethod.Get,
            $"api/v1/stock/products/{productId}/expiry" + (locationId is { } location ? $"?location_id={location}" : ""),
            null, authenticated: true, withDevice: false, ct);

    public Task<StockCard> GetStockCardAsync(
        Guid productId, Guid? locationId, DateOnly from, DateOnly to, int offset, int limit, CancellationToken ct = default) =>
        SendAsync<StockCard>(HttpMethod.Get,
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"api/v1/stock/card?product_id={productId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&offset={offset}&limit={limit}") +
            (locationId is { } location ? $"&location_id={location}" : ""),
            null, authenticated: true, withDevice: false, ct);

    public Task<LocationTransfer> CreateLocationTransferAsync(CreateLocationTransferRequest request, CancellationToken ct = default) =>
        SendAsync<LocationTransfer>(HttpMethod.Post, "api/v1/stock/transfers", request, authenticated: true, withDevice: false, ct);

    public Task<LocationTransferPreview> PreviewLocationTransferAsync(
        Guid fromLocationId, IReadOnlyList<LocationTransferLineRequest> lines, CancellationToken ct = default, Guid? toLocationId = null) =>
        SendAsync<LocationTransferPreview>(HttpMethod.Post, "api/v1/stock/transfers/preview",
            new { FromLocationId = fromLocationId, ToLocationId = toLocationId, Lines = lines }, authenticated: true, withDevice: false, ct);

    public async Task<IReadOnlyList<LocationTransferSummary>> GetLocationTransfersAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await SendAsync<List<LocationTransferSummary>>(HttpMethod.Get,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"api/v1/stock/transfers?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"),
            null, authenticated: true, withDevice: false, ct);

    public Task<LocationTransfer> GetLocationTransferAsync(Guid transferId, CancellationToken ct = default) =>
        SendAsync<LocationTransfer>(HttpMethod.Get, $"api/v1/stock/transfers/{transferId}", null, authenticated: true, withDevice: false, ct);

    public Task<CartCancellationResult> CancelCartAsync(
        Guid id, string reason, decimal totalAmount, IReadOnlyList<CartCancellationLine> lines, CancellationToken ct = default) =>
        SendAsync<CartCancellationResult>(HttpMethod.Post, "api/v1/cart-cancellations",
            new { Id = id, Reason = reason, TotalAmount = totalAmount, Lines = lines },
            authenticated: true, withDevice: true, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool authenticated, bool withDevice,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: PosJson.Options);
        if (authenticated && session.Token is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (withDevice)
        {
            if (settings.DeviceId is not { } deviceId)
                throw new NodeApiException(ErrorMessages.DeviceNotConfigured, "device_not_configured", null);
            request.Headers.Add(DeviceHeader, deviceId.ToString());
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new NodeApiException(ErrorMessages.Unreachable(settings.NodeServiceUrl), "node_unreachable", null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation.
            throw new NodeApiException(ErrorMessages.Unreachable(settings.NodeServiceUrl), "node_unreachable", null, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<T>(text, PosJson.Options)
                    ?? throw new NodeApiException("Jawaban Node Service kosong.", "empty_response", (int)response.StatusCode);
            }

            throw ToException(response.StatusCode, text);
        }
    }

    private static NodeApiException ToException(HttpStatusCode status, string body)
    {
        string? code = null, detail = null;
        int? retryAfter = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                retryAfter = root.TryGetProperty("retry_after_seconds", out var r) && r.TryGetInt32(out var seconds) ? seconds : null;
            }
        }
        catch (JsonException)
        {
            // not a problem document (e.g. a proxy error page)
        }

        return new NodeApiException(ErrorMessages.For(code, (int)status, detail, retryAfter), code, (int)status);
    }
}
