using Pos.NodeService.Auth;

namespace Pos.IntegrationTests;

/// <summary>
/// HQ (Laravel, PHP password_hash) writes users.password_hash; the node verifies it with BCrypt.Net. Fixed hashes from
/// both sides prove they read each other's format. The PHP side checks the C# hash in
/// src/hq/tests/Feature/BcryptCompatibilityTest.php. No database needed.
/// </summary>
public class PasswordHasherCompatibilityTests
{
    /// <summary>"rahasia-dari-hq", made by PHP 8.5 password_hash(PASSWORD_BCRYPT, cost 12) — what Laravel's Hash::make stores.</summary>
    private const string HashFromHq = "$2y$12$NFoawGeDM8gk7ZZK2oFFT.4KrzOzkYwGQgTIkzag0L78nnmpLUCsy";

    [Fact]
    public void A_hash_made_at_hq_in_php_verifies_at_the_node()
    {
        Assert.True(PasswordHasher.Verify("rahasia-dari-hq", HashFromHq));
        Assert.False(PasswordHasher.Verify("rahasia-dari-node", HashFromHq));
    }

    [Fact]
    public void The_node_makes_the_same_2y_format_with_cost_12()
    {
        var hash = PasswordHasher.Hash("admin123");

        Assert.StartsWith("$2y$12$", hash);
        Assert.Equal(60, hash.Length);
    }
}
