using Dapper;
using MySqlConnector;
using Pos.NodeService.Api;
using Pos.NodeService.Data;
using Pos.NodeService.Nodes;
using Pos.Shared.Auth;

namespace Pos.NodeService.Auth;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record PinLoginRequest(string? Username, string? Pin);

public sealed record AuthUserDto(Guid Id, string Username, string FullName);

public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    DateTime ExpiresAt,
    AuthUserDto User,
    IReadOnlyCollection<string> Permissions);

/// <summary>
/// Password and PIN login against local data only (works without internet). Failures are counted per
/// login name in <c>login_throttle</c>; 5 in a row lock the name for 5 minutes. Unknown, inactive and
/// deleted users fail exactly like a wrong password.
/// </summary>
public sealed class AuthService(TransactionRunner runner, NodeIdentity nodeIdentity, TokenService tokens, TimeProvider clock)
{
    private const string InvalidCredentialsMessage = "The username or the password/PIN is not correct.";

    public Task<LoginResponse> LoginWithPasswordAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        LoginAsync(request.Username, request.Password, usePin: false, cancellationToken);

    public Task<LoginResponse> LoginWithPinAsync(PinLoginRequest request, CancellationToken cancellationToken = default) =>
        LoginAsync(request.Username, request.Pin, usePin: true, cancellationToken);

    private async Task<LoginResponse> LoginAsync(string? username, string? secret, bool usePin, CancellationToken cancellationToken)
    {
        var node = await nodeIdentity.GetAsync(cancellationToken);
        var loginName = (username ?? "").Trim().ToLowerInvariant();
        if (loginName.Length > 50)
            loginName = loginName[..50];

        // The outcome is decided inside the transaction and turned into an error after commit,
        // so a failed attempt is still recorded.
        var outcome = await runner.RunAsync(
            (c, tx, ct) => AttemptAsync(c, tx, node, loginName, secret ?? "", usePin, ct), "Login", cancellationToken);

        return outcome switch
        {
            { Locked: { } remaining } => throw new ApiException(StatusCodes.Status429TooManyRequests, "account_locked",
                "Too many failed attempts. Try again later.",
                new Dictionary<string, object?> { ["retry_after_seconds"] = (int)Math.Ceiling(remaining.TotalSeconds) }),
            { User: null } => throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_credentials", InvalidCredentialsMessage),
            { HasNodeAccess: false } => throw new ApiException(StatusCodes.Status403Forbidden, "no_access_to_node",
                $"This user has no role at node {node.Code}."),
            _ => Issue(outcome.User!, outcome.Permissions!),
        };
    }

    private async Task<Outcome> AttemptAsync(
        MySqlConnection c, MySqlTransaction tx, NodeInfo node, string loginName, string secret, bool usePin, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        // Row lock per login name: parallel attempts for the same name are counted one by one.
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO login_throttle (login_name) VALUES (@loginName) ON DUPLICATE KEY UPDATE login_name = login_name",
            new { loginName }, tx, cancellationToken: ct));
        var row = await c.QuerySingleAsync<(int FailedCount, DateTime? LockedUntil)>(new CommandDefinition(
            "SELECT failed_count, locked_until FROM login_throttle WHERE login_name = @loginName FOR UPDATE",
            new { loginName }, tx, cancellationToken: ct));
        var state = new LoginThrottleState(row.FailedCount, row.LockedUntil);

        if (LoginThrottle.IsLocked(state, now))
            return new Outcome(Locked: LoginThrottle.Remaining(state, now));

        var user = loginName.Length == 0 ? null : await c.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            """
            SELECT id AS Id, username AS Username, full_name AS FullName, password_hash AS PasswordHash, pin_hash AS PinHash
            FROM users WHERE username = @loginName AND is_active = 1 AND deleted_at IS NULL
            """, new { loginName }, tx, cancellationToken: ct));

        // Always run one bcrypt verification so unknown users take as long as wrong passwords.
        var hash = user is null ? PasswordHasher.TimingDummyHash : usePin ? user.PinHash : user.PasswordHash;
        var verified = PasswordHasher.Verify(secret, hash ?? PasswordHasher.TimingDummyHash) && user is not null && hash is not null;

        if (!verified)
        {
            var failed = LoginThrottle.OnFailure(state, now);
            await SaveAsync(c, tx, loginName, failed, now, ct);
            return new Outcome();
        }

        await SaveAsync(c, tx, loginName, LoginThrottle.OnSuccess(), lastFailedAt: null, ct);

        var rolePermissions = (await c.QueryAsync<string?>(new CommandDefinition(
            """
            SELECT CAST(r.permissions AS CHAR)
            FROM user_roles ur JOIN roles r ON r.id = ur.role_id
            WHERE ur.user_id = @Id AND ur.deleted_at IS NULL AND r.deleted_at IS NULL
              AND (ur.node_id IS NULL OR ur.node_id = @nodeId)
            """, new { user!.Id, nodeId = node.Id }, tx, cancellationToken: ct))).ToList();

        return rolePermissions.Count == 0
            ? new Outcome(User: user, HasNodeAccess: false)
            : new Outcome(User: user, HasNodeAccess: true, Permissions: PermissionSet.FromRoleJson(rolePermissions));
    }

    private static Task SaveAsync(MySqlConnection c, MySqlTransaction tx, string loginName, LoginThrottleState state,
        DateTime? lastFailedAt, CancellationToken ct) =>
        c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE login_throttle
            SET failed_count = @FailedCount, locked_until = @LockedUntil, last_failed_at = COALESCE(@lastFailedAt, last_failed_at)
            WHERE login_name = @loginName
            """, new { state.FailedCount, state.LockedUntil, lastFailedAt, loginName }, tx, cancellationToken: ct));

    private LoginResponse Issue(UserRow user, PermissionSet permissions)
    {
        var (token, expiresAt) = tokens.Issue(user.Id, user.Username, permissions.Names, clock.GetUtcNow().UtcDateTime);
        return new LoginResponse(token, "Bearer", expiresAt, new AuthUserDto(user.Id, user.Username, user.FullName), permissions.Names);
    }

    private sealed record UserRow(Guid Id, string Username, string FullName, string PasswordHash, string? PinHash);

    private sealed record Outcome(
        TimeSpan? Locked = null, UserRow? User = null, bool HasNodeAccess = false, PermissionSet? Permissions = null);
}
