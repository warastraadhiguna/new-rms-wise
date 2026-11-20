using Microsoft.Extensions.Configuration.Json;
using Pos.NodeService.Data;
using Pos.NodeService.Health;
using Pos.NodeService.Stock;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored: holds ConnectionStrings:Pos. Placed right after the other JSON files so
// environment variables and command-line arguments still override it.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
var sources = builder.Configuration.Sources;
var localSettings = sources[^1];
sources.RemoveAt(sources.Count - 1);
sources.Insert(sources.ToList().FindLastIndex(s => s is JsonConfigurationSource) + 1, localSettings);

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
builder.Services.AddSingleton<StockPostingService>();

var app = builder.Build();
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pos.Startup");

if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Pos")))
{
    startupLog.LogCritical("ConnectionStrings:Pos is not configured. Add it to appsettings.Local.json. Stopping.");
    return 1;
}

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

app.MapHealthEndpoints();

app.Run();
return 0;

static string PosConnectionString(IConfiguration configuration) =>
    configuration.GetConnectionString("Pos")
    ?? throw new InvalidOperationException("ConnectionStrings:Pos is not configured. Add it to appsettings.Local.json.");

public partial class Program;
