namespace Pos.Desktop.Services;

public sealed record UserInfo(Guid Id, string Username, string FullName);

public sealed record NodeSummary(Guid Id, string Code, string Name);

/// <summary>Response of /auth/login and /auth/pin (snake_case JSON).</summary>
public sealed record LoginResult(
    string AccessToken,
    DateTime ExpiresAt,
    UserInfo User,
    IReadOnlyList<string> Permissions,
    NodeSummary Node);

public sealed record ShiftInfo(
    Guid Id,
    string Number,
    Guid CashierId,
    string Status,
    DateTime OpenedAt,
    decimal OpeningCash);

/// <summary>Node Service calls used by the cashier screens.</summary>
public interface INodeApi
{
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);

    Task<LoginResult> PinLoginAsync(string username, string pin, CancellationToken ct = default);

    /// <returns>The device's open shift, or null when none is open.</returns>
    Task<ShiftInfo?> GetCurrentShiftAsync(CancellationToken ct = default);

    Task<ShiftInfo> OpenShiftAsync(decimal openingCash, CancellationToken ct = default);
}

/// <summary>Switches between the top-level windows.</summary>
public interface INavigator
{
    void ShowLogin();

    void ShowOpenShift();

    void ShowMain();
}

/// <summary>The logged-in user, node and shift of this PC. One per application.</summary>
public sealed class SessionState
{
    public string? Token { get; private set; }
    public DateTime? TokenExpiresAt { get; private set; }
    public UserInfo? User { get; private set; }
    public NodeSummary? Node { get; private set; }
    public IReadOnlyList<string> Permissions { get; private set; } = [];
    public ShiftInfo? Shift { get; set; }

    public bool IsLoggedIn => Token is not null;

    public void SetLogin(LoginResult login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Token = login.AccessToken;
        TokenExpiresAt = login.ExpiresAt;
        User = login.User;
        Node = login.Node;
        Permissions = login.Permissions;
        Shift = null;
    }

    public void Clear()
    {
        Token = null;
        TokenExpiresAt = null;
        User = null;
        Node = null;
        Permissions = [];
        Shift = null;
    }
}
