using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Pos.NodeService.Auth;

/// <summary>
/// Issues and validates the node's JWTs (HS256 with the node's own key). Login works offline:
/// nothing here talks to HQ. Issuer and audience name the node, so a token from another node is refused.
/// </summary>
public sealed class TokenService
{
    public const string PermissionClaim = "perm";
    public const string NameClaim = "name";

    private readonly SymmetricSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(byte[] key, string nodeCode, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Token lifetime must be positive.");

        _key = new SymmetricSecurityKey(key);
        Issuer = Audience = $"pos-node:{nodeCode.Trim().ToUpperInvariant()}";
        Lifetime = lifetime;
    }

    public string Issuer { get; }
    public string Audience { get; }
    public TimeSpan Lifetime { get; }

    public (string Token, DateTime ExpiresAt) Issue(Guid userId, string username, IEnumerable<string> permissions, DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Time must be UTC.", nameof(now));

        var expiresAt = now + Lifetime;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(NameClaim, username),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim(PermissionClaim, p)));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expiresAt);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = _key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = NameClaim,
    };
}
