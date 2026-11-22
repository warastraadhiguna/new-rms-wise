using Microsoft.Extensions.Configuration.Json;
using MySqlConnector;
using Pos.NodeService.Api;
using Pos.NodeService.Auth;
using Pos.NodeService.Data;
using Pos.NodeService.Sales;
using Pos.NodeService.Shifts;
using Pos.NodeService.Dev;
using Pos.NodeService.Health;
using Pos.NodeService.Nodes;
using Pos.NodeService.Stock;
using Pos.NodeService.Sync;
using Pos.Shared.Sync;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored: holds ConnectionStrings:Pos and Node:Code. Placed right after the other JSON
// files so environment variables and command-line arguments still override it.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
var sources = builder.Configuration.Sources;
var localSettings = sources[^1];
sources.RemoveAt(sources.Count - 1);
sources.Insert(sources.ToList().FindLastIndex(s => s is JsonConfigurationSource) + 1, localSettings);

builder.Services.ConfigureHttpJsonOptions(o => PosJson.Configure(o.SerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);

// Read lazily (after Build) so test hosts and later configuration sources can override them.
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    return new DbConnectionFactory(
        PosConnectionString(configuration),
        configuration.GetValue<int?>("Database:LockWaitTimeoutSeconds"));
});
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    return new DatabaseMigrator(
        PosConnectionString(configuration),
        configuration["Database:MigrationsPath"] ?? Path.Combine(AppContext.BaseDirectory, "migrations"),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.Migrations"));
});
builder.Services.AddSingleton(sp => new NodeIdentity(
    sp.GetRequiredService<DbConnectionFactory>(),
    sp.GetRequiredService<IConfiguration>()["Node:Code"] ?? ""));
builder.Services.AddSingleton<TransactionRunner>();
builder.Services.AddSingleton(sp => new StockPostingService(sp.GetRequiredService<TransactionRunner>()));
builder.Services.AddSingleton<StockIntegrityStatus>();
builder.Services.AddSingleton<StockIntegrityService>();
builder.Services.AddSingleton<StockQueryService>();
builder.Services.AddSingleton<StockCardService>();
builder.Services.AddSingleton<LocationTransferService>();
builder.Services.AddHostedService<StockIntegrityWorker>();
builder.Services.AddPosAuth();
builder.Services.AddSingleton<ShiftService>();
builder.Services.AddSingleton(sp => new SaleService(
    sp.GetRequiredService<TransactionRunner>(), sp.GetRequiredService<NodeIdentity>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<AuthService>()));
builder.Services.AddSingleton<Pos.NodeService.Catalog.ProductSearchService>();
builder.Services.AddSingleton<CartCancellationService>();
builder.Services.AddSingleton<SalesConfigService>();
builder.Services.AddSingleton<SaleDetailService>();
builder.Services.AddSingleton<SalesListService>();
builder.Services.AddSingleton<SaleVoidService>();
builder.Services.AddSingleton<SaleReturnService>();
builder.Services.AddSingleton<Pos.NodeService.Customers.CustomerSearchService>();
// Sync with HQ (docs/sync-contract.md): section Sync, token in a DPAPI file, background worker.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Sync").Get<SyncOptions>() ?? new SyncOptions());
builder.Services.AddSingleton(sp => new SyncTokenStore(sp.GetRequiredService<SyncOptions>().TokenFile));
builder.Services.AddHttpClient(HqSyncClient.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip });
builder.Services.AddSingleton(sp => new HqSyncClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(HqSyncClient.HttpClientName),
    sp.GetRequiredService<SyncOptions>(), sp.GetRequiredService<SyncTokenStore>()));
builder.Services.AddSingleton<SyncStatus>();
builder.Services.AddSingleton<MasterDataApplier>();
builder.Services.AddSingleton<OutboxPusher>();
builder.Services.AddSingleton<SyncAgent>();
builder.Services.AddSingleton<NodeInitialization>();
builder.Services.AddSingleton<NodeInitializer>();
builder.Services.AddSingleton<SyncStatusService>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddSingleton<DevSeeder>(sp => new DevSeeder(
    sp.GetRequiredService<TransactionRunner>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.DevSeed")));

var app = builder.Build();
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.Startup");

// `Pos.NodeService set-sync-token <token>`: store the token from HQ (DPAPI), then exit. Needs no database.
if (args.Length > 0 && args[0] == "set-sync-token")
{
    if (args.Length < 2)
    {
        startupLog.LogCritical("Usage: Pos.NodeService set-sync-token <token from HQ, Toko & Gudang>");
        return 1;
    }
    var store = app.Services.GetRequiredService<SyncTokenStore>();
    store.Save(args[1]);
    startupLog.LogInformation("Sync token saved (encrypted) in {Path}.", store.Path);
    return 0;
}

if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Pos")))
{
    startupLog.LogCritical("ConnectionStrings:Pos is not configured. Add it to appsettings.Local.json. Stopping.");
    return 1;
}
if (string.IsNullOrWhiteSpace(app.Configuration["Node:Code"]))
{
    startupLog.LogCritical("Node:Code is not configured (e.g. \"TK01\"). Add it to appsettings.Local.json. Stopping.");
    return 1;
}

ServerVersionCheckResult server;
try
{
    server = await ServerVersionCheck.CheckAsync(app.Services.GetRequiredService<DbConnectionFactory>());
}
catch (MySqlException ex)
{
    startupLog.LogCritical("Cannot connect to the database: {Error}. Node Service stopped.", ex.Message);
    return 1;
}
if (!server.Supported)
{
    startupLog.LogCritical("Unsupported database server: {Reason} Node Service stopped.", server.Reason);
    return 1;
}
startupLog.LogInformation("Database server: {Product} {Version}.", server.Product, server.Version);

var migration = app.Services.GetRequiredService<DatabaseMigrator>().Migrate();
if (!migration.Successful)
{
    if (migration.FailedScript is null)
        startupLog.LogCritical("Database migration could not start: {Error}. Node Service stopped.", migration.Error);
    else
        startupLog.LogCritical("Database migration failed in script {Script}: {Error}. Node Service stopped; no later script was applied.",
            migration.FailedScript, migration.Error);
    return 1;
}
startupLog.LogInformation("Database migrations applied: {Count} ({Scripts}).",
    migration.ScriptsApplied.Count, migration.ScriptsApplied.Count == 0 ? "up to date" : string.Join(", ", migration.ScriptsApplied));

var notInnoDb = await StorageEngineCheck.NonInnoDbTablesAsync(app.Services.GetRequiredService<DbConnectionFactory>());
if (notInnoDb.Count > 0)
{
    startupLog.LogCritical("Tables that are not InnoDB: {Tables}. Node Service stopped; convert them with ALTER TABLE ... ENGINE=InnoDB.",
        string.Join(", ", notInnoDb));
    return 1;
}

// The node's signing key is created on the first start (or loaded) before any request arrives.
try
{
    var tokens = app.Services.GetRequiredService<TokenService>();
    startupLog.LogInformation("Access tokens: issuer {Issuer}, lifetime {Hours} h, key {Path}.",
        tokens.Issuer, tokens.Lifetime.TotalHours, app.Configuration["Auth:KeyPath"] ?? SigningKeyStore.DefaultPath);
}
catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
{
    startupLog.LogCritical("Cannot load or create the JWT signing key: {Error}. Node Service stopped.", ex.Message);
    return 1;
}

// `dotnet run -- seed-dev`: development data only, then exit. Refuses databases not named *_dev.
if (args.Contains("seed-dev"))
{
    var database = new MySqlConnectionStringBuilder(PosConnectionString(app.Configuration)).Database;
    if (!database.EndsWith("_dev", StringComparison.OrdinalIgnoreCase))
    {
        startupLog.LogCritical("seed-dev refused: database '{Database}' does not end with '_dev'.", database);
        return 1;
    }
    var nodeCode = app.Services.GetRequiredService<NodeIdentity>().Code;
    await app.Services.GetRequiredService<DevSeeder>().SeedAsync(nodeCode);
    return 0;
}

// `init-from-hq`: an empty node database gets all master data from HQ, then the node may sell.
if (args.Contains("init-from-hq"))
{
    var result = await app.Services.GetRequiredService<NodeInitializer>().RunAsync(CancellationToken.None);
    if (result.Success)
        startupLog.LogInformation("{Message}", result.Message);
    else
        startupLog.LogCritical("init-from-hq failed: {Message}", result.Message);
    return result.Success ? 0 : 1;
}

// `add-device "<name>"`: registers an active cashier PC of this node and prints its id (put it in the PC's settings).
if (args.Length > 0 && args[0] == "add-device")
{
    var name = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "Kasir PC";
    var node = await app.Services.GetRequiredService<NodeIdentity>().GetAsync();
    var deviceId = Guid.CreateVersion7();
    await using (var c = await app.Services.GetRequiredService<DbConnectionFactory>().OpenAsync())
    {
        await Dapper.SqlMapper.ExecuteAsync(c,
            """
            INSERT INTO devices (id, node_id, name, type, fingerprint, public_key, status, approved_at)
            VALUES (@deviceId, @nodeId, @name, 'CASHIER_PC', @fingerprint, '', 'ACTIVE', UTC_TIMESTAMP(3))
            """, new { deviceId, nodeId = node.Id, name, fingerprint = "MANUAL-" + deviceId.ToString("N") });
    }
    startupLog.LogInformation("Device \"{Name}\" registered for node {Node}: device id {DeviceId}.", name, node.Code, deviceId);
    return 0;
}

// Before init-from-hq this node's row does not exist yet; anything that needs it (also the per-request user check in
// authentication) answers 503 node_not_configured instead of an error page.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (NodeNotFoundException ex) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        await Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "node_not_configured",
            extensions: new Dictionary<string, object?> { ["code"] = "node_not_configured" }).ExecuteAsync(context);
    }
});
// Explicit, so authentication runs inside the handler above (otherwise it is added in front of it).
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints(server);
app.MapApiV1();

app.Run();
return 0;

static string PosConnectionString(IConfiguration configuration) =>
    configuration.GetConnectionString("Pos")
    ?? throw new InvalidOperationException("ConnectionStrings:Pos is not configured. Add it to appsettings.Local.json.");

public partial class Program;
