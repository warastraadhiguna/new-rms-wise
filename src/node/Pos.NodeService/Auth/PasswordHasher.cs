namespace Pos.NodeService.Auth;

/// <summary>
/// bcrypt for passwords and PINs, compatible with Laravel's default hasher at HQ
/// (<c>$2y$</c> prefix, cost 12). Hashes arrive with master data, so nodes mostly verify.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultWorkFactor = 12;

    public static string Hash(string secret, int workFactor = DefaultWorkFactor)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return BCrypt.Net.BCrypt.HashPassword(secret, BCrypt.Net.BCrypt.GenerateSalt(workFactor, 'y'));
    }

    /// <summary>False for a missing or malformed hash (e.g. a disabled account marker), never throws.</summary>
    public static bool Verify(string secret, string? hash)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(hash) || !hash.StartsWith("$2", StringComparison.Ordinal))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(secret, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    /// <summary>A real hash to verify against when the user does not exist, so timing reveals nothing.</summary>
    public static readonly string TimingDummyHash = Hash("timing-dummy-" + Guid.NewGuid(), DefaultWorkFactor);
}
