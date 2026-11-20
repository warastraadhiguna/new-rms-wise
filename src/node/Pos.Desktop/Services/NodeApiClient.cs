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

    public Task<ShiftInfo> OpenShiftAsync(decimal openingCash, CancellationToken ct = default) =>
        SendAsync<ShiftInfo>(HttpMethod.Post, "api/v1/shifts/open", new { OpeningCash = openingCash },
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
