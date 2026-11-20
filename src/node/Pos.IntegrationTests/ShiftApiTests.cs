using System.Net;
using Dapper;
using Pos.IntegrationTests.Infrastructure;
using Pos.Shared.Sync;

namespace Pos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class ShiftApiTests(DatabaseFixture db) : IAsyncLifetime
{
    private NodeServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        if (TestDatabase.IsConfigured)
        {
            _factory = new NodeServiceFactory(db);
            _client = _factory.CreateClient();
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    private Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> Open(Guid device, decimal openingCash = 200000m) =>
        _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = openingCash }, db.CashierToken(), device);

    private Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> Close(Guid device, object body) =>
        _client.PostAsync("/api/v1/shifts/close", body, db.CashierToken(), device);

    [DatabaseFact]
    public async Task Open_creates_shift_with_number()
    {
        var device = await db.CreateDeviceAsync();

        var (status, body) = await Open(device);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("OPEN", body.Str("status"));
        Assert.Matches(@"^TK01-SH-\d{6}-\d{6}$", body.Str("number"));
        Assert.Equal(200000m, body.Dec("opening_cash"));
        Assert.Equal(7, body.Id().Version);
        Assert.EndsWith("Z", body.Str("opened_at"));

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync_outbox WHERE doc_type = @type AND doc_id = @id AND row_version = 1",
            new { type = SyncDocTypes.CashierShift, id = body.Id() }));
    }

    [DatabaseFact]
    public async Task Second_open_shift_on_same_device_is_rejected()
    {
        var device = await db.CreateDeviceAsync();
        var (_, first) = await Open(device);

        var (status, body) = await Open(device);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("shift_already_open", body.Str("code"));
        Assert.Equal(first.Id(), body.Id("shift_id"));
    }

    [DatabaseFact]
    public async Task Other_devices_can_open_their_own_shift()
    {
        var (s1, _) = await Open(await db.CreateDeviceAsync());
        var (s2, _) = await Open(await db.CreateDeviceAsync());

        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.Created, s2);
    }

    [DatabaseFact]
    public async Task Parallel_opens_on_one_device_create_exactly_one_shift()
    {
        var device = await db.CreateDeviceAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Open(device)));

        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.Status));
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cashier_shifts WHERE device_id = @device AND status = 'OPEN'", new { device }));
    }

    [DatabaseFact]
    public async Task Negative_opening_cash_is_rejected()
    {
        var (status, body) = await Open(await db.CreateDeviceAsync(), openingCash: -1m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("invalid_amount", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Close_without_sales_expects_the_opening_cash()
    {
        var device = await db.CreateDeviceAsync();
        var (_, opened) = await Open(device, openingCash: 150000m);

        var (status, closed) = await Close(device,
            new { ShiftId = opened.Id(), CountedCash = 149500m, Notes = "kurang 500" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("CLOSED", closed.Str("status"));
        Assert.Equal(150000m, closed.Dec("expected_cash"));
        Assert.Equal(149500m, closed.Dec("counted_cash"));
        Assert.Equal(-500m, closed.Dec("difference"));

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
            "SELECT MAX(row_version) FROM sync_outbox WHERE doc_id = @id", new { id = opened.Id() }));

        // the device may open a new shift after closing
        Assert.Equal(HttpStatusCode.Created, (await Open(device)).Status);
    }

    [DatabaseFact]
    public async Task Closing_twice_or_unknown_shift_is_rejected()
    {
        var device = await db.CreateDeviceAsync();
        var (_, opened) = await Open(device);
        await Close(device, new { ShiftId = opened.Id(), CountedCash = 200000m });

        var (again, againBody) = await Close(device, new { ShiftId = opened.Id(), CountedCash = 200000m });
        var (unknown, unknownBody) = await Close(device, new { ShiftId = Guid.CreateVersion7(), CountedCash = 0m });

        Assert.Equal((HttpStatusCode.Conflict, "shift_already_closed"), (again, againBody.Str("code")));
        Assert.Equal((HttpStatusCode.NotFound, "unknown_shift"), (unknown, unknownBody.Str("code")));
    }
}
