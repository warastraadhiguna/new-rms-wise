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
builder.Services.AddSingleton<Pos.NodeService.Customers.CustomerSearchService>();
builder.Services.AddSingleton<DevSeeder>(sp => new DevSeeder(
    sp.GetRequiredService<TransactionRunner>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.DevSeed")));

var app = builder.Build();
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.Startup");

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

app.MapHealthEndpoints(server);
app.MapApiV1();

app.Run();
return 0;

static string PosConnectionString(IConfiguration configuration) =>
    configuration.GetConnectionString("Pos")
    ?? throw new InvalidOperationException("ConnectionStrings:Pos is not configured. Add it to appsettings.Local.json.");

public partial class Program;
