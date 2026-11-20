using System.Security.Cryptography;

namespace Pos.NodeService.Auth;

/// <summary>
/// The node's own JWT signing key: 64 random bytes created on first start and kept on disk
/// (default <c>%ProgramData%\PosNodeService\keys\jwt-signing.key</c>, setting <c>Auth:KeyPath</c>).
/// On Windows the file is encrypted with DPAPI (machine scope), so a copied file is useless elsewhere.
/// The key never leaves the node and is never committed; replacing it logs everybody out.
/// </summary>
public static class SigningKeyStore
{
    public const int KeyBytes = 64;

    private static readonly byte[] Entropy = "Pos.NodeService.JwtSigningKey.v1"u8.ToArray();
    private static readonly Lock Gate = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PosNodeService", "keys", "jwt-signing.key");

    public static byte[] LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (Gate)
        {
            if (File.Exists(path))
                return Unprotect(File.ReadAllBytes(path));

            var key = RandomNumberGenerator.GetBytes(KeyBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            // CreateNew: if another process won the race, use its key instead of overwriting it.
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Protect(key));
            }
            catch (IOException) when (File.Exists(path))
            {
                return Unprotect(File.ReadAllBytes(path));
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }
    }

    private static byte[] Protect(byte[] key) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(key, Entropy, DataProtectionScope.LocalMachine) : key;

    private static byte[] Unprotect(byte[] stored)
    {
        var key = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.LocalMachine) : stored;
        return key.Length == KeyBytes
            ? key
            : throw new CryptographicException("The JWT signing key file is damaged; delete it to create a new key (all users must log in again).");
    }
}
