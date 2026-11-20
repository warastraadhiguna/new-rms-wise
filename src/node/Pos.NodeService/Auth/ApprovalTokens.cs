using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;

namespace Pos.NodeService.Auth;

/// <summary>
/// One-time supervisor approvals (<c>approval_tokens</c>, node only). Only a SHA-256 hash of the token is
/// stored. A token is valid for one action, for the user it was issued to, until it expires or is used.
/// </summary>
public static class ApprovalTokens
{
    public static string NewToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Marks the token used inside the caller's transaction (a rollback makes it usable again).
    /// </summary>
    /// <returns>The approving supervisor, or null when the token is unknown, expired, used, for another action or another user.</returns>
    public static async Task<Guid?> ConsumeAsync(
        MySqlConnection c, MySqlTransaction tx, string token, string action, Guid requestedBy, Guid docId, DateTime now,
        CancellationToken ct)
    {
        var hash = Hash(token);
        var used = await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE approval_tokens SET used_at = @now, used_for_doc_id = @docId
            WHERE token_hash = @hash AND action = @action AND requested_by = @requestedBy
              AND used_at IS NULL AND expires_at > @now
            """, new { now, docId, hash, action, requestedBy }, tx, cancellationToken: ct));
        if (used != 1)
            return null;

        return await c.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT approved_by FROM approval_tokens WHERE token_hash = @hash", new { hash }, tx, cancellationToken: ct));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
