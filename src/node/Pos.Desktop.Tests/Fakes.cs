using System.Net;
using System.Text;
using Pos.Desktop.Services;

namespace Pos.Desktop.Tests;

/// <summary>A fake Node Service: records requests and answers with canned responses.</summary>
public sealed class FakeServer : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public FakeServer Reply(HttpStatusCode status, string json)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, status >= HttpStatusCode.BadRequest ? "application/problem+json" : "application/json"),
        });
        return this;
    }

    public FakeServer Problem(HttpStatusCode status, string code, string extra = "") =>
        Reply(status, $$"""{"title":"{{code}}","status":{{(int)status}},"detail":"server detail","code":"{{code}}"{{extra}}}""");

    public FakeServer Throw(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        if (_responses.Count == 0)
            throw new InvalidOperationException($"No response prepared for {request.Method} {request.RequestUri}");
        return _responses.Dequeue()(request);
    }
}

/// <summary>Scriptable INodeApi for ViewModel tests.</summary>
public sealed class FakeNodeApi : INodeApi
{
    public Func<string, string, LoginResult>? OnLogin { get; set; }
    public Func<string, string, LoginResult>? OnPinLogin { get; set; }
    public Func<ShiftInfo?>? OnCurrentShift { get; set; }
    public Func<decimal, ShiftInfo>? OnOpenShift { get; set; }

    public List<string> Calls { get; } = [];

    public Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        Calls.Add($"login:{username}");
        return Task.FromResult(OnLogin!(username, password));
    }

    public Task<LoginResult> PinLoginAsync(string username, string pin, CancellationToken ct = default)
    {
        Calls.Add($"pin:{username}");
        return Task.FromResult(OnPinLogin!(username, pin));
    }

    public Task<ShiftInfo?> GetCurrentShiftAsync(CancellationToken ct = default)
    {
        Calls.Add("current-shift");
        return Task.FromResult(OnCurrentShift!());
    }

    public Task<ShiftInfo> OpenShiftAsync(decimal openingCash, CancellationToken ct = default)
    {
        Calls.Add($"open-shift:{openingCash}");
        return Task.FromResult(OnOpenShift!(openingCash));
    }
}

public sealed class FakeNavigator : INavigator
{
    public List<string> Shown { get; } = [];

    public void ShowLogin() => Shown.Add("login");
    public void ShowOpenShift() => Shown.Add("open-shift");
    public void ShowMain() => Shown.Add("main");
}

public static class Samples
{
    public static readonly Guid UserId = Guid.Parse("01930000-0000-7000-8000-000000000001");
    public static readonly Guid OtherUserId = Guid.Parse("01930000-0000-7000-8000-000000000002");
    public static readonly Guid DeviceId = Guid.Parse("01930000-0000-7000-8000-0000000000d1");

    public static LoginResult Login(Guid? userId = null) => new(
        "jwt-token", new DateTime(2026, 11, 20, 15, 0, 0, DateTimeKind.Utc),
        new UserInfo(userId ?? UserId, "kasir1", "Kasir Satu"),
        ["sale.create", "shift.open"],
        new NodeSummary(Guid.CreateVersion7(), "TK01", "Toko Dev"));

    public static ShiftInfo Shift(Guid? cashierId = null) => new(
        Guid.CreateVersion7(), "TK01-SH-202611-000007", cashierId ?? UserId, "OPEN",
        new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc), 200000m);
}
