using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>HTTP helpers that speak the Node Service JSON conventions.</summary>
public static class ApiClient
{
    public static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(this HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body, PosJson.Options);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static decimal Dec(this JsonElement element, string property) =>
        decimal.Parse(element.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    public static Guid Id(this JsonElement element, string property = "id") => element.GetProperty(property).GetGuid();
}
