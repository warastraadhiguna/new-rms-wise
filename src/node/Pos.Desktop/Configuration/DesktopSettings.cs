using System.IO;
using System.Text.Json;

namespace Pos.Desktop.Configuration;

/// <param name="NodeServiceUrl">Base address of the store's Node Service, always ending with '/'.</param>
/// <param name="DeviceId">This PC's device id as registered by the store admin.</param>
public sealed record DesktopSettings(string NodeServiceUrl, Guid? DeviceId);

public sealed record DesktopSettingsLoadResult(DesktopSettings? Settings, string? Error);

/// <summary>
/// Per-PC settings in <c>%ProgramData%\PosDesktop\settings.json</c> (never in the repository).
/// A missing file is created as a template and reported, so the installer/admin knows what to fill in.
/// Messages are Indonesian because they are shown to store staff.
/// </summary>
public sealed class DesktopSettingsStore(string path)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosDesktop", "settings.json");

    private const string Template = """
        {
          "node_service_url": "http://localhost:5080",
          "device_id": ""
        }
        """;

    public string FilePath => path;

    public DesktopSettingsLoadResult Load()
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, Template);
            return Fail($"File konfigurasi belum diisi. Isi alamat Node Service (node_service_url) dan ID perangkat (device_id) di {path}, lalu jalankan ulang aplikasi.");
        }

        string? url;
        string? device;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            url = Read(doc.RootElement, "node_service_url");
            device = Read(doc.RootElement, "device_id");
        }
        catch (JsonException ex)
        {
            return Fail($"File konfigurasi {path} tidak dapat dibaca: {ex.Message}");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Fail($"node_service_url di {path} harus berupa alamat http/https, misalnya http://192.168.1.10:5080.");
        if (!Guid.TryParse(device, out var deviceId))
            return Fail($"device_id di {path} harus berupa ID perangkat (UUID) yang didaftarkan admin toko.");

        var baseUrl = uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
        return new DesktopSettingsLoadResult(new DesktopSettings(baseUrl, deviceId), null);
    }

    private static string? Read(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DesktopSettingsLoadResult Fail(string error) => new(null, error);
}
