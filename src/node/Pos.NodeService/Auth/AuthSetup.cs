using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pos.Shared.Auth;

namespace Pos.NodeService.Auth;

/// <summary>JWT bearer authentication and one authorization policy per permission.</summary>
public static class AuthSetup
{
    public static IServiceCollection AddPosAuth(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var key = SigningKeyStore.LoadOrCreate(configuration["Auth:KeyPath"] ?? SigningKeyStore.DefaultPath);
            var hours = configuration.GetValue<double?>("Auth:TokenLifetimeHours") ?? 12;
            return new TokenService(key, configuration["Node:Code"] ?? "", TimeSpan.FromHours(hours));
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        // Resolved lazily so the key and node code come from the final configuration.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService>((options, tokens) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokens.ValidationParameters();
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = RecheckUserAsync,
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        var (code, detail) = context.AuthenticateFailure switch
                        {
                            SecurityTokenExpiredException => ("token_expired", "The access token has expired; log in again."),
                            UserInactiveException => ("user_inactive", "This user is no longer active."),
                            _ => ("unauthorized", "A valid access token is required."),
                        };
                        await WriteProblemAsync(context.Response, StatusCodes.Status401Unauthorized, code, detail);
                    },
                    OnForbidden = context => WriteProblemAsync(context.Response, StatusCodes.Status403Forbidden,
                        "forbidden", "You do not have permission for this action."),
                };
            });

        services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { Permissions.ShiftOpen, Permissions.ShiftClose, Permissions.SaleCreate })
            {
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser()
                    .RequireAssertion(context => context.User.HasPermission(permission)));
            }
        });

        services.AddSingleton(sp => new UserAccessCache(
            sp.GetRequiredService<Data.DbConnectionFactory>(),
            sp.GetRequiredService<Nodes.NodeIdentity>(),
            sp.GetRequiredService<TimeProvider>(),
            UserAccessCache.TtlFrom(sp.GetRequiredService<IConfiguration>().GetValue<int?>("Auth:UserCacheSeconds"))));
        services.AddSingleton<AuthService>();
        return services;
    }

    /// <summary>
    /// Every authenticated request: the user must still be active and not deleted, and only the token's
    /// permissions that the user still has at this node remain (revocations apply within the cache time).
    /// </summary>
    private static async Task RecheckUserAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var access = context.HttpContext.RequestServices.GetRequiredService<UserAccessCache>();
        var current = await access.GetAsync(principal.UserId(), context.HttpContext.RequestAborted);
        if (current is null)
        {
            context.Fail(new UserInactiveException());
            return;
        }

        var effective = PermissionSet.FromNames(principal.FindAll(TokenService.PermissionClaim).Select(c => c.Value)).Intersect(current);
        var identity = new ClaimsIdentity(
            principal.Claims.Where(c => c.Type != TokenService.PermissionClaim)
                .Concat(effective.Names.Select(p => new Claim(TokenService.PermissionClaim, p))),
            principal.Identity?.AuthenticationType, TokenService.NameClaim, ClaimTypes.Role);
        context.Principal = new ClaimsPrincipal(identity);
    }

    /// <summary>True when the (re-checked) user has the permission or the wildcard.</summary>
    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.FindAll(TokenService.PermissionClaim).Any(c => c.Value == permission || c.Value == Permissions.All);

    /// <summary>The logged-in user's id (<c>sub</c>).</summary>
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new InvalidOperationException("The access token has no valid subject.");

    private static Task WriteProblemAsync(HttpResponse response, int status, string code, string detail) =>
        Results.Problem(detail: detail, statusCode: status, title: code,
                extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(response.HttpContext);
}
