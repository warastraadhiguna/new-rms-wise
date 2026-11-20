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
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        var expired = context.AuthenticateFailure is SecurityTokenExpiredException;
                        await WriteProblemAsync(context.Response, StatusCodes.Status401Unauthorized,
                            expired ? "token_expired" : "unauthorized",
                            expired ? "The access token has expired; log in again." : "A valid access token is required.");
                    },
                    OnForbidden = context => WriteProblemAsync(context.Response, StatusCodes.Status403Forbidden,
                        "forbidden", "You do not have permission for this action."),
                };
            });

        services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { Permissions.ShiftOpen, Permissions.ShiftClose, Permissions.SaleCreate })
            {
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                    context.User.FindAll(TokenService.PermissionClaim)
                        .Any(c => c.Value == permission || c.Value == Permissions.All)));
            }
        });

        services.AddSingleton<AuthService>();
        return services;
    }

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
