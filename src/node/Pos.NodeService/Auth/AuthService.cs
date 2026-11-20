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

public sealed record NodeDto(Guid Id, string Code, string Name);

public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    DateTime ExpiresAt,
    AuthUserDto User,
    IReadOnlyCollection<string> Permissions,
    NodeDto Node);

/// <param name="Action">One of <see cref="Permissions.ApprovalActions"/>.</param>
/// <param name="Username">Supervisor username.</param>
/// <param name="Password">Supervisor password (same as the supervisor's login password).</param>
public sealed record ApproveRequest(string? Action, string? Username, string? Password);

/// <param name="ApprovalToken">One-time token for <c>Action</c>, valid until <c>ExpiresAt</c>, only for the requesting user.</param>
public sealed record ApprovalResponse(string ApprovalToken, string Action, DateTime ExpiresAt, AuthUserDto ApprovedBy);

/// <summary>
/// Password and PIN login against local data only (works without internet). Failures are counted per
/// login name in <c>login_throttle</c>; 5 in a row lock the name for 5 minutes. Unknown, inactive and
/// deleted users fail exactly like a wrong password.
/// </summary>
public sealed class AuthService(TransactionRunner runner, NodeIdentity nodeIdentity, TokenService tokens, TimeProvider clock)
{
    private const string InvalidCredentialsMessage = "The username or the password/PIN is not correct.";

    /// <summary>How long an approval token stays usable.</summary>
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Checks a supervisor's username + password for one action (same lockout as login). The supervisor needs the
    /// action's permission at this node. Wrong credentials are 403 here, not 401, so the cashier's own session stays valid.
    /// </summary>
    public async Task<AuthUserDto> VerifyApproverAsync(string? username, string? password, string action, CancellationToken cancellationToken = default)
    {
        var node = await nodeIdentity.GetAsync(cancellationToken);
        var loginName = Normalize(username);
        var outcome = await runner.RunAsync(
            (c, tx, ct) => AttemptAsync(c, tx, node, loginName, password ?? "", usePin: false, ct), "Approval", cancellationToken);

        return outcome switch
        {
            { Locked: { } remaining } => throw Locked(remaining),
            { User: null } => throw new ApiException(StatusCodes.Status403Forbidden, "approval_rejected",
                "The supervisor username or password is not correct."),
            { Permissions: null } or { HasNodeAccess: false } => throw NotPermitted(action),
            _ when !outcome.Permissions!.Has(action) => throw NotPermitted(action),
            _ => new AuthUserDto(outcome.User!.Id, outcome.User.Username, outcome.User.FullName),
        };
    }

    /// <summary>POST /auth/approve: a supervisor approves one action for the logged-in user with a one-time token.</summary>
    public async Task<ApprovalResponse> ApproveAsync(ApproveRequest request, Guid requestedBy, CancellationToken cancellationToken = default)
    {
        var action = (request.Action ?? "").Trim().ToLowerInvariant();
        if (!Permissions.ApprovalActions.Contains(action))
            throw ApiException.Unprocessable("invalid_action",
                $"Action must be one of: {string.Join(", ", Permissions.ApprovalActions)}.");

        var approver = await VerifyApproverAsync(request.Username, request.Password, action, cancellationToken);
        var token = ApprovalTokens.NewToken();
        var expiresAt = clock.GetUtcNow().UtcDateTime + ApprovalLifetime;

        await runner.RunAsync((c, tx, ct) => c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO approval_tokens (token_hash, action, approved_by, requested_by, expires_at)
            VALUES (@hash, @action, @approvedBy, @requestedBy, @expiresAt)
            """,
            new { hash = ApprovalTokens.Hash(token), action, approvedBy = approver.Id, requestedBy, expiresAt },
            tx, cancellationToken: ct)), "Approval token", cancellationToken);

        return new ApprovalResponse(token, action, expiresAt, approver);
    }

    private static ApiException Locked(TimeSpan remaining) =>
        new(StatusCodes.Status429TooManyRequests, "account_locked", "Too many failed attempts. Try again later.",
            new Dictionary<string, object?> { ["retry_after_seconds"] = (int)Math.Ceiling(remaining.TotalSeconds) });

    private static ApiException NotPermitted(string action) =>
        new(StatusCodes.Status403Forbidden, "approval_not_permitted", $"This supervisor may not approve '{action}'.");

    private static string Normalize(string? username)
    {
        var loginName = (username ?? "").Trim().ToLowerInvariant();
        return loginName.Length > 50 ? loginName[..50] : loginName;
    }

    public Task<LoginResponse> LoginWithPasswordAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        LoginAsync(request.Username, request.Password, usePin: false, cancellationToken);

    public Task<LoginResponse> LoginWithPinAsync(PinLoginRequest request, CancellationToken cancellationToken = default) =>
        LoginAsync(request.Username, request.Pin, usePin: true, cancellationToken);

    private async Task<LoginResponse> LoginAsync(string? username, string? secret, bool usePin, CancellationToken cancellationToken)
    {
        var node = await nodeIdentity.GetAsync(cancellationToken);
        var loginName = Normalize(username);

        // The outcome is decided inside the transaction and turned into an error after commit,
        // so a failed attempt is still recorded.
        var outcome = await runner.RunAsync(
            (c, tx, ct) => AttemptAsync(c, tx, node, loginName, secret ?? "", usePin, ct), "Login", cancellationToken);

        return outcome switch
        {
            { Locked: { } remaining } => throw Locked(remaining),
            { User: null } => throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_credentials", InvalidCredentialsMessage),
            { HasNodeAccess: false } => throw new ApiException(StatusCodes.Status403Forbidden, "no_access_to_node",
                $"This user has no role at node {node.Code}."),
            _ => Issue(outcome.User!, outcome.Permissions!, node),
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

        var rolePermissions = await UserAccessQueries.RolePermissionsAsync(c, tx, user!.Id, node.Id, ct);

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

    private LoginResponse Issue(UserRow user, PermissionSet permissions, NodeInfo node)
    {
        var (token, expiresAt) = tokens.Issue(user.Id, user.Username, permissions.Names, clock.GetUtcNow().UtcDateTime);
        return new LoginResponse(token, "Bearer", expiresAt, new AuthUserDto(user.Id, user.Username, user.FullName),
            permissions.Names, new NodeDto(node.Id, node.Code, node.Name));
    }

    private sealed record UserRow(Guid Id, string Username, string FullName, string PasswordHash, string? PinHash);

    private sealed record Outcome(
        TimeSpan? Locked = null, UserRow? User = null, bool HasNodeAccess = false, PermissionSet? Permissions = null);
}
