using BootstrapJob.Bootstrap;
using BootstrapJob.Shopify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PcaData;
using SyncData;

// -------------------------------------------------------------------------
// Shopify Bootstrap Import Job
//
// Usage:
//   dotnet run                  — full import
//   dotnet run -- --dry-run     — build JSONL only, no Shopify/SQL writes
//   dotnet run -- --force       — truncate ProductSyncMap and re-import
//   dotnet run -- --list-locations — print Shopify locations and exit
// -------------------------------------------------------------------------

Console.WriteLine("=== Shopify Bootstrap Import Job ===\n");

// --- Args ---
bool dryRun      = args.Contains("--dry-run");
bool force       = args.Contains("--force");
bool listLocs    = args.Contains("--list-locations");

// --- Configuration ---
var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddCommandLine(args)
    .Build();

// --- Validate required config ---
var storeUrl = config["Shopify:StoreUrl"];
var accessToken = config["Shopify:AccessToken"];
var pcaConn = config.GetConnectionString("PcAmerica");
var syncConn = config.GetConnectionString("ShopifySync");

var missing = new List<string>();
if (string.IsNullOrWhiteSpace(storeUrl))    missing.Add("Shopify:StoreUrl");
if (string.IsNullOrWhiteSpace(accessToken)) missing.Add("Shopify:AccessToken");
if (string.IsNullOrWhiteSpace(pcaConn))     missing.Add("ConnectionStrings:PcAmerica");
if (string.IsNullOrWhiteSpace(syncConn))    missing.Add("ConnectionStrings:ShopifySync");

if (missing.Count > 0)
{
    Console.WriteLine("ERROR — Missing required configuration:");
    foreach (var key in missing)
        Console.WriteLine($"  {key}");
    Console.WriteLine("\nAdd missing values to appsettings.local.json.");
    return 1;
}

// --- DI Setup ---
var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(config);
services.AddPcAmericaDb(config);
services.AddSyncDb(config);
services.AddHttpClient<ShopifyClient>();
services.AddTransient<JsonlBuilder>();
services.AddTransient<SyncMapWriter>();
services.AddTransient<BootstrapOrchestrator>();

var provider = services.BuildServiceProvider();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var scope = provider.CreateAsyncScope();

// --- --list-locations shortcut ---
if (listLocs)
{
    var shopify = scope.ServiceProvider.GetRequiredService<ShopifyClient>();
    var locations = await shopify.FetchLocationsAsync(cts.Token);
    Console.WriteLine("Shopify locations:");
    foreach (var loc in locations)
        Console.WriteLine($"  {loc.Id,-20} {loc.Name}");
    return 0;
}

// --- --force: truncate ProductSyncMap ---
if (force)
{
    var syncDb = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
    var count = await syncDb.ProductSyncMap.CountAsync(cts.Token);
    if (count > 0)
    {
        Console.WriteLine($"--force: deleting {count:N0} existing ProductSyncMap rows...");
        await syncDb.Database.ExecuteSqlRawAsync("DELETE FROM ProductSyncMap", cts.Token);
        Console.WriteLine("Done.\n");
    }
}

// --- Run ---
try
{
    var orchestrator = scope.ServiceProvider.GetRequiredService<BootstrapOrchestrator>();
    await orchestrator.RunAsync(dryRun, cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nCancelled.");
    return 2;
}
catch (Exception ex)
{
    Console.WriteLine($"\nFATAL: {ex.Message}");
    return 1;
}
