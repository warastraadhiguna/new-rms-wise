using System.IO;
using System.Text.Json;

namespace Pos.Desktop.Configuration;

/// <param name="NodeServiceUrl">Base address of the store's Node Service, always ending with '/'.</param>
/// <param name="DeviceId">This PC's device id as registered by the store admin.</param>
/// <param name="PrinterName">Windows printer that receives RAW ESC/POS receipts; empty = show the receipt on screen.</param>
/// <param name="ReceiptWidth">Characters per receipt line: 48 for 80 mm paper (default), 32 for 58 mm.</param>
public sealed record DesktopSettings(string NodeServiceUrl, Guid? DeviceId, string? PrinterName = null, int ReceiptWidth = 48);

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
          "device_id": "",
          "printer_name": "",
          "receipt_width": 48
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
        string? printer;
        int width;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            url = Read(doc.RootElement, "node_service_url");
            device = Read(doc.RootElement, "device_id");
            printer = Read(doc.RootElement, "printer_name");
            if (!TryReadWidth(doc.RootElement, out width))
                return Fail($"receipt_width di {path} harus angka {Pos.Shared.Printing.ReceiptFormatter.MinWidth}..80 (48 untuk kertas 80 mm, 32 untuk 58 mm).");
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
        return new DesktopSettingsLoadResult(
            new DesktopSettings(baseUrl, deviceId, string.IsNullOrWhiteSpace(printer) ? null : printer.Trim(), width), null);
    }

    private static string? Read(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Missing = 48; a number (or numeric string) between the minimum width and 80.</summary>
    private static bool TryReadWidth(JsonElement root, out int width)
    {
        width = 48;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("receipt_width", out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        var ok = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out width),
            JsonValueKind.String => int.TryParse(value.GetString(), out width),
            _ => false,
        };
        return ok && width is >= Pos.Shared.Printing.ReceiptFormatter.MinWidth and <= 80;
    }

    private static DesktopSettingsLoadResult Fail(string error) => new(null, error);
}
