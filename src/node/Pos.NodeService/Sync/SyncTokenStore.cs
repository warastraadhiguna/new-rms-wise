using System.Security.Cryptography;
using System.Text;

namespace Pos.NodeService.Sync;

/// <summary>
/// The node's sync token (from HQ, Toko &amp; Gudang → "Buat token sinkron"), saved with
/// <c>Pos.NodeService set-sync-token &lt;token&gt;</c>. On Windows the file is encrypted with DPAPI (machine scope), so a
/// copied file is useless on another PC. Never in appsettings, never committed.
/// </summary>
public sealed class SyncTokenStore(string path)
{
    private static readonly byte[] Entropy = "Pos.NodeService.SyncToken.v1"u8.ToArray();

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosNodeService", "keys", "sync-token.dat");

    public string Path { get; } = path;

    public void Save(string token)
    {
        token = token.Trim();
        if (!token.StartsWith("pos_", StringComparison.Ordinal) || token.Length < 20)
            throw new ArgumentException("A sync token starts with \"pos_\" (copy it from HQ, Toko & Gudang).", nameof(token));

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        File.WriteAllBytes(Path, Protect(Encoding.UTF8.GetBytes(token)));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <returns>The token, or null when none was saved.</returns>
    /// <exception cref="CryptographicException">The file was copied from another PC or is damaged.</exception>
    public string? Load() => File.Exists(Path) ? Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(Path))) : null;

    private static byte[] Protect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine) : data;

    private static byte[] Unprotect(byte[] data) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine) : data;
}
