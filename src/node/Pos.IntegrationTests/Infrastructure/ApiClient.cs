using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>HTTP helpers that speak the Node Service JSON conventions.</summary>
public static class ApiClient
{
    public const string DeviceHeader = "X-Device-Id";

    /// <summary>POST JSON, optionally with a bearer token and the device header.</summary>
    public static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        this HttpClient client, string url, object body, string? token = null, Guid? device = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, body.GetType(), options: PosJson.Options),
        };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (device is not null)
            request.Headers.Add(DeviceHeader, device.Value.ToString());

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(
        this HttpClient client, string url, string? token = null, Guid? device = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (device is not null)
            request.Headers.Add(DeviceHeader, device.Value.ToString());

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static decimal Dec(this JsonElement element, string property) =>
        decimal.Parse(element.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    public static Guid Id(this JsonElement element, string property = "id") => element.GetProperty(property).GetGuid();
}
