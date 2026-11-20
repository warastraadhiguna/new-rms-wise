using System.IO;
using System.Text.Json;
using Pos.Shared.Sales;
using Pos.Shared.Sync;

namespace Pos.Desktop.Services;

/// <summary>Unpaid cart saved after every change, to survive a crash or power cut.</summary>
/// <param name="PendingSaleId">
/// Sale id of a payment that was sent but not confirmed (network failure, crash). Paying the same cart again
/// reuses it, so the server returns the stored sale instead of creating a second one. Any cart change clears it.
/// </param>
public sealed record CartSnapshot(IReadOnlyList<CartSnapshotLine> Lines, CustomerItem? Customer, Guid? PendingSaleId = null);

public sealed record CartSnapshotLine(CartItem Item, decimal Qty);

public interface ICartStore
{
    CartSnapshot? Load();

    void Save(CartSnapshot snapshot);

    void Clear();
}

/// <summary>"Atur Tampilan": optional columns of the product grid (RMS 2 defaults: only Satuan).</summary>
public sealed record SalesViewSettings(
    bool ShowBrand = false,
    bool ShowShelf = false,
    bool ShowStock = false,
    bool ShowCategory = false,
    bool ShowDiscountPrice = false,
    bool ShowUnit = true);

public interface IViewSettingsStore
{
    SalesViewSettings Load();

    void Save(SalesViewSettings settings);
}

/// <summary>
/// Small JSON files under <c>%LocalAppData%\PosDesktop</c>. A damaged file is treated as missing, so a
/// bad write can never stop the cashier from working.
/// </summary>
public sealed class JsonFileStore<T>(string path) where T : class
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PosDesktop");

    public string FilePath => path;

    public T? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), PosJson.Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Write then replace, so a crash during the write keeps the previous file.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, PosJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

public sealed class FileCartStore(string path) : ICartStore
{
    private readonly JsonFileStore<CartSnapshot> _file = new(path);

    public static string PathFor(Guid? deviceId) =>
        Path.Combine(JsonFileStore<CartSnapshot>.DefaultFolder, $"cart-{deviceId?.ToString() ?? "default"}.json");

    public CartSnapshot? Load() => _file.Load();

    public void Save(CartSnapshot snapshot) => _file.Save(snapshot);

    public void Clear() => _file.Delete();
}

public sealed class FileViewSettingsStore(string path) : IViewSettingsStore
{
    private readonly JsonFileStore<SalesViewSettings> _file = new(path);

    public static string DefaultPath => Path.Combine(JsonFileStore<SalesViewSettings>.DefaultFolder, "sales-view.json");

    public SalesViewSettings Load() => _file.Load() ?? new SalesViewSettings();

    public void Save(SalesViewSettings settings) => _file.Save(settings);
}
