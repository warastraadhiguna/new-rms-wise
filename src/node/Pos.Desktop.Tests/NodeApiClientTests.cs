using System.Net;
using System.Text.Json;
using Pos.Desktop.Configuration;
using Pos.Desktop.Services;

namespace Pos.Desktop.Tests;

public class NodeApiClientTests
{
    private readonly FakeServer _server = new();
    private readonly SessionState _session = new();

    private NodeApiClient Client() => new(
        new HttpClient(_server) { BaseAddress = new Uri("http://kasir-server:5080/") },
        _session,
        new DesktopSettings("http://kasir-server:5080/", Samples.DeviceId));

    private const string LoginJson = """
        {"access_token":"jwt-abc","token_type":"Bearer","expires_at":"2026-11-20T15:00:00.000Z",
         "user":{"id":"01930000-0000-7000-8000-000000000001","username":"kasir1","full_name":"Kasir Satu"},
         "permissions":["sale.create","shift.open"],
         "node":{"id":"01930000-0000-7000-8000-0000000000aa","code":"TK01","name":"Toko Dev"}}
        """;

    private const string ShiftJson = """
        {"id":"01930000-0000-7000-8000-000000000051","number":"TK01-SH-202611-000007",
         "cashier_id":"01930000-0000-7000-8000-000000000001","device_id":"01930000-0000-7000-8000-0000000000d1",
         "status":"OPEN","opened_at":"2026-11-20T01:00:00.000Z","opening_cash":"200000.0000"}
        """;

    [Fact]
    public async Task Login_posts_snake_case_json_and_reads_the_result()
    {
        _server.Reply(HttpStatusCode.OK, LoginJson);

        var result = await Client().LoginAsync("kasir1", "rahasia");

        var (request, body) = Assert.Single(_server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/auth/login", request.RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("kasir1", json.RootElement.GetProperty("username").GetString());
        Assert.Equal("rahasia", json.RootElement.GetProperty("password").GetString());

        Assert.Equal("jwt-abc", result.AccessToken);
        Assert.Equal("Kasir Satu", result.User.FullName);
        Assert.Equal("TK01", result.Node.Code);
        Assert.Equal(DateTimeKind.Utc, result.ExpiresAt.Kind);
    }

    [Fact]
    public async Task Requests_carry_the_token_and_the_device_id()
    {
        _session.SetLogin(Samples.Login());
        _server.Reply(HttpStatusCode.OK, ShiftJson);

        await Client().GetCurrentShiftAsync();

        var request = Assert.Single(_server.Requests).Request;
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("jwt-token", request.Headers.Authorization.Parameter);
        Assert.Equal(Samples.DeviceId.ToString(), Assert.Single(request.Headers.GetValues("X-Device-Id")));
    }

    [Fact]
    public async Task Opening_cash_is_sent_as_a_decimal_string()
    {
        _session.SetLogin(Samples.Login());
        _server.Reply(HttpStatusCode.Created, ShiftJson);

        var shift = await Client().OpenShiftAsync(200000m);

        var body = Assert.Single(_server.Requests).Body;
        Assert.Contains("\"opening_cash\":\"200000.0000\"", body);
        Assert.Equal(200000m, shift.OpeningCash);
        Assert.Equal("TK01-SH-202611-000007", shift.Number);
    }

    [Fact]
    public async Task No_open_shift_is_null()
    {
        _session.SetLogin(Samples.Login());
        _server.Problem(HttpStatusCode.NotFound, "no_open_shift");

        Assert.Null(await Client().GetCurrentShiftAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_credentials", "Username atau password salah.")]
    [InlineData(HttpStatusCode.Forbidden, "no_access_to_node", "Pengguna ini tidak punya akses ke toko ini.")]
    [InlineData(HttpStatusCode.Forbidden, "device_not_allowed", "Perangkat ini belum terdaftar atau belum aktif di toko ini. Hubungi admin.")]
    [InlineData(HttpStatusCode.Unauthorized, "token_expired", "Sesi login sudah berakhir. Silakan login kembali.")]
    public async Task Problem_codes_become_indonesian_messages(HttpStatusCode status, string code, string message)
    {
        _server.Problem(status, code);

        var ex = await Assert.ThrowsAsync<NodeApiException>(() => Client().LoginAsync("kasir1", "x"));

        Assert.Equal(code, ex.Code);
        Assert.Equal((int)status, ex.StatusCode);
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task Lockout_message_says_how_long_to_wait()
    {
        _server.Problem(HttpStatusCode.TooManyRequests, "account_locked", ""","retry_after_seconds":170""");

        var ex = await Assert.ThrowsAsync<NodeApiException>(() => Client().LoginAsync("kasir1", "x"));

        Assert.Equal("Terlalu banyak percobaan gagal. Coba lagi dalam 3 menit.", ex.Message);
    }

    [Fact]
    public async Task Unknown_code_and_server_error_still_give_a_message()
    {
        _server.Problem(HttpStatusCode.UnprocessableEntity, "something_new");
        _server.Reply(HttpStatusCode.InternalServerError, "oops");

        var unknown = await Assert.ThrowsAsync<NodeApiException>(() => Client().LoginAsync("kasir1", "x"));
        var crash = await Assert.ThrowsAsync<NodeApiException>(() => Client().LoginAsync("kasir1", "x"));

        Assert.Equal("Node Service menolak permintaan: server detail (something_new).", unknown.Message);
        Assert.Equal("Node Service mengalami gangguan (HTTP 500). Coba lagi atau hubungi admin.", crash.Message);
    }

    [Fact]
    public async Task Unreachable_node_service_has_a_clear_message()
    {
        _server.Throw(new HttpRequestException("No connection could be made"));

        var ex = await Assert.ThrowsAsync<NodeApiException>(() => Client().LoginAsync("kasir1", "x"));

        Assert.Equal("node_unreachable", ex.Code);
        Assert.Equal(
            "Node Service tidak dapat dihubungi di http://kasir-server:5080/. Pastikan komputer server toko menyala dan jaringan tersambung.",
            ex.Message);
    }

    [Fact]
    public async Task Timeout_is_reported_like_an_unreachable_server()
    {
        _server.Throw(new TaskCanceledException("timeout", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<NodeApiException>(() => Client().LoginAsync("kasir1", "x"));

        Assert.Equal("node_unreachable", ex.Code);
    }

    [Fact]
    public async Task Missing_device_id_is_reported_before_calling_the_server()
    {
        var client = new NodeApiClient(new HttpClient(_server) { BaseAddress = new Uri("http://kasir-server:5080/") },
            _session, new DesktopSettings("http://kasir-server:5080/", null));
        _session.SetLogin(Samples.Login());

        var ex = await Assert.ThrowsAsync<NodeApiException>(() => client.GetCurrentShiftAsync());

        Assert.Equal("device_not_configured", ex.Code);
        Assert.Empty(_server.Requests);
    }
}
