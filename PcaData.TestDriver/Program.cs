using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcaData;
using SyncData;
using SyncData.Models;
using SyncJob;

// -------------------------------------------------------------------------
// PCAmerica DB Connection Test Driver
// Usage: dotnet run -- "Server=.;Database=...;User Id=...;Password=...;"
//        OR set "PcAmerica" in appsettings.json and run with no args
// -------------------------------------------------------------------------

Console.WriteLine("=== PCAmerica DB Test Driver ===\n");

// --- Configuration ---
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddCommandLine(args, new Dictionary<string, string>
    {
        ["--connstr"] = "ConnectionStrings:PcAmerica"
    })
    .Build();

// Allow passing connection string as first positional arg for quick testing
if (args.Length > 0 && !args[0].StartsWith("--"))
{
    Environment.SetEnvironmentVariable("OVERRIDE_CONNSTR", args[0]);
}

var overrideConn = Environment.GetEnvironmentVariable("OVERRIDE_CONNSTR");
if (overrideConn is not null)
{
    Console.WriteLine("[config] Using connection string from command-line argument.\n");
    var inMemConfig = new Dictionary<string, string?>
        { ["ConnectionStrings:PcAmerica"] = overrideConn };
    config = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemConfig)
        .Build();
}

// --- DI Setup ---
var services = new ServiceCollection();
services.AddPcAmericaDb(config);
services.AddPcAmericaWriteDb(config);
var provider = services.BuildServiceProvider();

await using var scope = provider.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<PcAmericaDbContext>();

// -------------------------------------------------------------------------
// TEST 1: Basic connectivity
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 1] Checking database connectivity...");
try
{
    var canConnect = await db.Database.CanConnectAsync();
    Console.WriteLine(canConnect
        ? "  PASS — connected successfully.\n"
        : "  FAIL — CanConnectAsync returned false.\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    return;
}

// -------------------------------------------------------------------------
// TEST 2: Row count in Inventory (excluding soft-deleted)
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 2] Counting active Inventory rows (IsDeleted = false)...");
try
{
    var count = await db.Inventory
        .Where(x => !x.IsDeleted)
        .CountAsync();
    Console.WriteLine($"  PASS — {count:N0} active items found.\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — {ex.Message}\n");
}

// -------------------------------------------------------------------------
// TEST 3: Sample 5 items — verify column mapping
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 3] Fetching top 5 active items...");
try
{
    var items = await db.Inventory
        .Where(x => !x.IsDeleted)
        .OrderBy(x => x.ItemNum)
        .Take(5)
        .Select(x => new { x.ItemNum, x.ItemName, x.InStock })
        .ToListAsync();

    Console.WriteLine($"  {"ItemNum",-20} {"ItemName",-40} {"In_Stock",10}");
    Console.WriteLine($"  {new string('-', 72)}");
    foreach (var item in items)
        Console.WriteLine($"  {item.ItemNum,-20} {item.ItemName,-40} {item.InStock,10:F2}");

    Console.WriteLine($"\n  PASS — {items.Count} rows returned.\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — {ex.Message}\n");
}

// -------------------------------------------------------------------------
// TEST 4: Sample SKU/UPC join
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 4] Joining Inventory → Inventory_SKUS (top 5 with UPCs)...");
try
{
    var skus = await db.Inventory
        .Where(x => !x.IsDeleted)
        .Join(db.InventorySkus,
            inv => inv.ItemNum,
            sku => sku.ItemNum,
            (inv, sku) => new { inv.ItemNum, inv.ItemName, sku.AltSku })
        .Where(x => x.AltSku != null && x.AltSku != "")
        .Take(5)
        .ToListAsync();

    Console.WriteLine($"  {"ItemNum",-20} {"ItemName",-35} {"AltSKU (UPC)",15}");
    Console.WriteLine($"  {new string('-', 72)}");
    foreach (var row in skus)
        Console.WriteLine($"  {row.ItemNum,-20} {row.ItemName,-35} {row.AltSku,15}");

    Console.WriteLine(skus.Count > 0
        ? $"\n  PASS — {skus.Count} rows with UPCs found.\n"
        : "\n  WARN — 0 rows returned. Inventory_SKUS may be empty or join key mismatch.\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — {ex.Message}\n");
}

// -------------------------------------------------------------------------
// TEST 5: Verify SaveChanges guard throws
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 5] Verifying SaveChanges guard (must throw)...");
try
{
    db.SaveChanges();
    Console.WriteLine("  FAIL — SaveChanges did NOT throw. Guard is missing!\n");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("read-only"))
{
    Console.WriteLine($"  PASS — SaveChanges correctly threw: \"{ex.Message}\"\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  WARN — Threw unexpected exception type: {ex.GetType().Name}: {ex.Message}\n");
}

// -------------------------------------------------------------------------
// TEST 6: Items with zero or negative stock (sanity check for Phase 6)
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 6] Checking for zero/negative stock items...");
try
{
    var zeroCount = await db.Inventory
        .Where(x => !x.IsDeleted && x.InStock <= 0)
        .CountAsync();

    var negCount = await db.Inventory
        .Where(x => !x.IsDeleted && x.InStock < 0)
        .CountAsync();

    Console.WriteLine($"  Items with stock <= 0 : {zeroCount:N0}");
    Console.WriteLine($"  Items with stock <  0 : {negCount:N0}");
    Console.WriteLine(negCount == 0
        ? "  PASS — No negative stock values.\n"
        : $"  WARN — {negCount} items have negative stock. Phase 6 sanity check will reject these.\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — {ex.Message}\n");
}

Console.WriteLine("=== PCAmerica tests complete ===\n");

// =========================================================================
// SYNC DB TESTS
// =========================================================================

Console.WriteLine("=== SyncData DB Tests ===\n");

// --- SyncData DI Setup ---
var syncServices = new ServiceCollection();
syncServices.AddSyncDb(config);
var syncProvider = syncServices.BuildServiceProvider();

await using var syncScope = syncProvider.CreateAsyncScope();
var syncDb = syncScope.ServiceProvider.GetRequiredService<SyncDbContext>();

// -------------------------------------------------------------------------
// TEST 7: Apply pending migrations (creates ShopifySync DB if needed)
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 7] Applying pending migrations to ShopifySync DB...");
try
{
    await syncDb.Database.MigrateAsync();
    Console.WriteLine("  PASS — Migrations applied (or already up to date).\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — {ex.Message}\n");
    return;
}

// -------------------------------------------------------------------------
// TEST 8: ProductSyncMap — insert, query, delete
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 8] Smoke-testing ProductSyncMap (insert → query → delete)...");
try
{
    var row = new ProductSyncMap
    {
        PcaItemNum          = "TEST-ITEM-001",
        PcaUpc              = "123456789012",
        ShopifyProductId    = 1,
        ShopifyVariantId    = 2,
        ShopifyInventoryItemId = 3,
        ShopifyLocationId   = 4,
        LastKnownQty        = 10.0m,
        LastSyncedAt        = DateTime.UtcNow
    };
    syncDb.ProductSyncMap.Add(row);
    await syncDb.SaveChangesAsync();

    var found = await syncDb.ProductSyncMap.FirstOrDefaultAsync(x => x.PcaItemNum == "TEST-ITEM-001");
    if (found is null) throw new Exception("Row not found after insert.");

    syncDb.ProductSyncMap.Remove(found);
    await syncDb.SaveChangesAsync();

    Console.WriteLine("  PASS — Insert/query/delete succeeded.\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — {ex.Message}\n");
}

// -------------------------------------------------------------------------
// TEST 9: SyncUnmatched — insert, query, delete
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 9] Smoke-testing SyncUnmatched (insert → query → delete)...");
try
{
    var row = new SyncUnmatched
    {
        PcaItemNum  = "TEST-ITEM-002",
        PcaItemName = "Test Item",
        PcaUpc      = null,
        Reason      = "No matching Shopify variant found.",
        LoggedAt    = DateTime.UtcNow
    };
    syncDb.SyncUnmatched.Add(row);
    await syncDb.SaveChangesAsync();

    var found = await syncDb.SyncUnmatched.FirstOrDefaultAsync(x => x.PcaItemNum == "TEST-ITEM-002");
    if (found is null) throw new Exception("Row not found after insert.");

    syncDb.SyncUnmatched.Remove(found);
    await syncDb.SaveChangesAsync();

    Console.WriteLine("  PASS — Insert/query/delete succeeded.\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — {ex.Message}\n");
}

// -------------------------------------------------------------------------
// TEST 10: SyncState — upsert singleton row
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 10] Smoke-testing SyncState (upsert Id=1)...");
try
{
    var state = await syncDb.SyncState.FindAsync(1);
    if (state is null)
    {
        state = new SyncState { Id = 1, LastPolledAt = null };
        syncDb.SyncState.Add(state);
    }
    else
    {
        state.LastPolledAt = DateTime.UtcNow;
        syncDb.SyncState.Update(state);
    }
    await syncDb.SaveChangesAsync();

    var check = await syncDb.SyncState.FindAsync(1);
    if (check is null) throw new Exception("SyncState row not found after upsert.");

    Console.WriteLine($"  PASS — SyncState Id=1 exists. LastPolledAt = {check.LastPolledAt?.ToString("u") ?? "null"}\n");
}
catch (Exception ex)
{
    Console.WriteLine($"  FAIL — {ex.Message}\n");
}

Console.WriteLine("=== Test run complete ===");

// =========================================================================
// SYNCJOB TESTS
// =========================================================================

Console.WriteLine("\n=== SyncJob Tests ===\n");

static HttpMessageHandler FakeShopifyWithDelay(string responseJson, int delayMs) =>
    new CancellableFakeHandler(responseJson, delayMs);

// Helper: builds the inventorySetQuantities success response JSON.
static string ShopifySuccess() => """
    {
      "data": {
        "inventorySetQuantities": {
          "inventoryAdjustmentGroup": { "id": "gid://shopify/InventoryAdjustmentGroup/1" },
          "userErrors": []
        }
      }
    }
    """;

// Helper: builds a response with one COMPARE_QUANTITY_STALE error at index 0.
static string ShopifyStale() => """
    {
      "data": {
        "inventorySetQuantities": {
          "inventoryAdjustmentGroup": null,
          "userErrors": [
            {
              "code": "COMPARE_QUANTITY_STALE",
              "field": ["quantities", "0", "compareQuantity"],
              "message": "Compare quantity is stale."
            }
          ]
        }
      }
    }
    """;

// Helper: builds a nodes query response with a single InventoryItem at the given quantity.
static string ShopifyInventoryQueryResponse(long inventoryItemId, int quantity) => $$"""
    {
      "data": {
        "nodes": [
          {
            "id": "gid://shopify/InventoryItem/{{inventoryItemId}}",
            "inventoryLevel": {
              "quantities": [
                { "name": "available", "quantity": {{quantity}} }
              ]
            }
          }
        ]
      }
    }
    """;

// Helper: builds a nodes query response with an empty nodes array (no items).
static string ShopifyInventoryQueryEmpty() => """{"data":{"nodes":[]}}""";

// Helper: builds a response with one INVALID_INVENTORY_ITEM error at index 0.
static string ShopifyNotFound() => """
    {
      "data": {
        "inventorySetQuantities": {
          "inventoryAdjustmentGroup": null,
          "userErrors": [
            {
              "code": "INVALID_INVENTORY_ITEM",
              "field": ["quantities", "0", "inventoryItemId"],
              "message": "Inventory item not found."
            }
          ]
        }
      }
    }
    """;

// Shared: seed a synthetic ProductSyncMap row pointing at a known PCA item, then clean up.
// Returns the inserted row's Id, or 0 if no PCA items exist.
/// <summary>
/// Borrows an existing ProductSyncMap row (matching a PCA item with stock > 0),
/// overwrites its Shopify IDs and LastKnown values so the test sees a delta,
/// and returns the modified row plus a snapshot for restoration.
/// </summary>
async Task<(ProductSyncMap? row, SyncMapSnapshot? snapshot)> SeedSyncMapRowAsync(SyncDbContext db, PcAmericaDbContext pca)
{
    var item = await pca.Inventory
        .Where(x => !x.IsDeleted && x.InStock > 0)
        .OrderBy(x => x.ItemNum)
        .FirstOrDefaultAsync();

    if (item is null) return (null, null);

    var row = await db.ProductSyncMap
        .FirstOrDefaultAsync(m => m.PcaItemNum == item.ItemNum);

    if (row is null) return (null, null);

    // Snapshot original values for restoration
    var snapshot = new SyncMapSnapshot(
        row.ShopifyInventoryItemId, row.ShopifyLocationId,
        row.LastKnownQty, row.LastKnownPcaQty, row.LastKnownShopifyQty, row.LastSyncedAt);

    // Use a LastKnownQty that differs from InStock so it's always "changed"
    var differentQty = item.InStock + 99;
    row.ShopifyInventoryItemId = 1001;
    row.ShopifyLocationId = 88892145881L;
    row.LastKnownQty = differentQty;
    row.LastKnownPcaQty = differentQty;
    row.LastKnownShopifyQty = differentQty;
    row.LastSyncedAt = DateTime.UtcNow.AddDays(-1);
    await db.SaveChangesAsync();

    return (row, snapshot);
}

async Task RestoreSyncMapRowAsync(SyncDbContext db, int id, SyncMapSnapshot snapshot)
{
    var row = await db.ProductSyncMap.FindAsync(id);
    if (row is not null)
    {
        row.ShopifyInventoryItemId = snapshot.ShopifyInventoryItemId;
        row.ShopifyLocationId = snapshot.ShopifyLocationId;
        row.LastKnownQty = snapshot.LastKnownQty;
        row.LastKnownPcaQty = snapshot.LastKnownPcaQty;
        row.LastKnownShopifyQty = snapshot.LastKnownShopifyQty;
        row.LastSyncedAt = snapshot.LastSyncedAt;
        await db.SaveChangesAsync();
    }
}

// Re-use the existing DB contexts from the SyncData tests above.
// Build a minimal logging factory (suppresses noise in test output).
var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

PcaWriteDbContext BuildTestPcaWriteDb()
{
    var connStr = config.GetConnectionString("PcAmerica")
        ?? throw new InvalidOperationException("Missing PcAmerica connection string.");
    var opts = new DbContextOptionsBuilder<PcaWriteDbContext>()
        .UseSqlServer(connStr)
        .Options;
    return new PcaWriteDbContext(opts);
}

SyncOrchestrator BuildOrchestrator(SyncDbContext syncDbCtx, HttpMessageHandler handler, PcaWriteDbContext? pcaWriteDb = null)
{
    // Use the test constructor — bypasses Shopify config parsing, fake handler is injected directly.
    var http = new HttpClient(handler)
    {
        BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
        Timeout = Timeout.InfiniteTimeSpan, // let CancellationToken control cancellation, not HttpClient timeout
    };
    var shopifyClient = new SyncJob.Shopify.ShopifyClient(http);
    var writeDb = pcaWriteDb ?? BuildTestPcaWriteDb();
    return new SyncOrchestrator(
        db,
        writeDb,
        syncDbCtx,
        shopifyClient,
        config,
        loggerFactory.CreateLogger<SyncOrchestrator>());
}

// -------------------------------------------------------------------------
// TEST 11: Happy path — one changed item, Shopify returns success
//   Verifies: changed item detected, pushed, LastKnownQty updated
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 11] Happy path — changed item pushed successfully...");
{
    await using var scope11 = syncProvider.CreateAsyncScope();
    var syncDb11 = scope11.ServiceProvider.GetRequiredService<SyncDbContext>();

    var (seeded, origSnapshot) = await SeedSyncMapRowAsync(syncDb11, db);
    if (seeded is null)
    {
        Console.WriteLine("  SKIP — no active PCA items with positive stock.\n");
        goto test12;
    }

    try
    {
        // Shopify query returns LastKnownQty (no Shopify-side delta), mutation succeeds
        var queryJson = ShopifyInventoryQueryResponse(1001, (int)seeded.LastKnownQty);
        var orch = BuildOrchestrator(syncDb11, new BidirectionalFakeHandler(queryJson, ShopifySuccess()));
        // Capture before RunAsync — orchestrator mutates the tracked entity in place
        var originalQty = seeded.LastKnownQty;
        var result = await orch.RunAsync(CancellationToken.None);
        var refreshed = await syncDb11.ProductSyncMap
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seeded.Id);

        if (!result.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false. FatalError: {result.FatalError}\n");
        else if (result.PushedToShopify != 1)
            Console.WriteLine($"  FAIL — Expected PushedToShopify=1, got {result.PushedToShopify}\n");
        else if (refreshed is null || refreshed.LastKnownQty == originalQty)
            Console.WriteLine($"  FAIL — LastKnownQty was not updated after push (still {originalQty}).\n");
        else
            Console.WriteLine($"  PASS — Pushed 1 item. LastKnownQty updated from {originalQty} to {refreshed.LastKnownQty}.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb11, seeded.Id, origSnapshot!);
    }
}

test12:
// -------------------------------------------------------------------------
// TEST 12: No-change path — item in sync map but qty already matches
//   Verifies: zero items pushed, SyncResult still success
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 12] No-change path — qty already matches, nothing pushed...");
{
    await using var scope12 = syncProvider.CreateAsyncScope();
    var syncDb12 = scope12.ServiceProvider.GetRequiredService<SyncDbContext>();

    var item = await db.Inventory
        .Where(x => !x.IsDeleted && x.InStock >= 0)
        .OrderBy(x => x.ItemNum)
        .FirstOrDefaultAsync();

    if (item is null)
    {
        Console.WriteLine("  SKIP — no PCA items.\n");
        goto test13;
    }

    var row = await syncDb12.ProductSyncMap
        .FirstOrDefaultAsync(m => m.PcaItemNum == item.ItemNum);

    if (row is null)
    {
        Console.WriteLine("  SKIP — no sync map row for first PCA item.\n");
        goto test13;
    }

    var snap12 = new SyncMapSnapshot(
        row.ShopifyInventoryItemId, row.ShopifyLocationId,
        row.LastKnownQty, row.LastKnownPcaQty, row.LastKnownShopifyQty, row.LastSyncedAt);

    // Set LastKnownQty to match PCA InStock — no delta
    row.LastKnownQty = item.InStock;
    row.LastKnownPcaQty = item.InStock;
    row.LastKnownShopifyQty = item.InStock;
    row.LastSyncedAt = DateTime.UtcNow;
    await syncDb12.SaveChangesAsync();

    try
    {
        // Shopify query returns same qty as LastKnownQty — no delta on either side
        var noChangeQty = (int)Math.Max(0, Math.Truncate(row.LastKnownQty));
        var queryJson12 = ShopifyInventoryQueryResponse(row.ShopifyInventoryItemId, noChangeQty);
        var orch = BuildOrchestrator(syncDb12, new BidirectionalFakeHandler(queryJson12, ShopifySuccess()));
        var result = await orch.RunAsync(CancellationToken.None);

        if (!result.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false.\n");
        else if (result.PushedToShopify != 0)
            Console.WriteLine($"  FAIL — Expected PushedToShopify=0, got {result.PushedToShopify}\n");
        else
            Console.WriteLine($"  PASS — 0 items pushed (qty unchanged).\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb12, row.Id, snap12);
    }
}

test13:
// -------------------------------------------------------------------------
// TEST 13: Not-in-sync-map path — PCA item exists but no ProductSyncMap row
//   Verifies: NotInSyncMapCount > 0, no errors, no push, result is success
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 13] Not-in-sync-map path — PCA item skipped, counter incremented...");
{
    await using var scope13 = syncProvider.CreateAsyncScope();
    var syncDb13 = scope13.ServiceProvider.GetRequiredService<SyncDbContext>();

    // Delete all ProductSyncMap rows for this scope so all PCA items are unmatched.
    // We use a fresh scope so this doesn't affect other tests — but ProductSyncMap
    // rows from previous tests should already be cleaned up. Confirm it's empty.
    var existingCount = await syncDb13.ProductSyncMap.CountAsync();
    if (existingCount > 0)
    {
        Console.WriteLine($"  SKIP — ProductSyncMap has {existingCount} rows from production data; cannot safely empty it for this test.\n");
        goto test14;
    }

    var pcaCount = await db.Inventory.Where(x => !x.IsDeleted).CountAsync();

    try
    {
        var orch = BuildOrchestrator(syncDb13, new BidirectionalFakeHandler(ShopifyInventoryQueryEmpty(), ShopifySuccess()));
        var result = await orch.RunAsync(CancellationToken.None);

        if (!result.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false.\n");
        else if (result.NotInSyncMapCount != pcaCount)
            Console.WriteLine($"  FAIL — Expected NotInSyncMapCount={pcaCount}, got {result.NotInSyncMapCount}\n");
        else if (result.PushedToShopify != 0)
            Console.WriteLine($"  FAIL — Expected PushedToShopify=0, got {result.PushedToShopify}\n");
        else
            Console.WriteLine($"  PASS — {result.NotInSyncMapCount} items correctly counted as not-in-sync-map.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
}

test14:
// -------------------------------------------------------------------------
// TEST 14: Conflict retry path — Shopify returns COMPARE_QUANTITY_STALE on
//   first pass, success on unconditional retry
//   Verifies: item counted as pushed after successful retry
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 14] Conflict retry path — COMPARE_QUANTITY_STALE → retry succeeds...");
{
    await using var scope14 = syncProvider.CreateAsyncScope();
    var syncDb14 = scope14.ServiceProvider.GetRequiredService<SyncDbContext>();

    var (seeded, origSnapshot) = await SeedSyncMapRowAsync(syncDb14, db);
    if (seeded is null)
    {
        Console.WriteLine("  SKIP — no active PCA items.\n");
        goto test15;
    }

    try
    {
        // Query returns LastKnownQty (no Shopify delta); mutations: first STALE, second success
        var queryJson14 = ShopifyInventoryQueryResponse(seeded.ShopifyInventoryItemId, (int)seeded.LastKnownQty);
        var handler = new BidirectionalSequentialHandler(queryJson14, [ShopifyStale(), ShopifySuccess()]);
        var orch = BuildOrchestrator(syncDb14, handler);
        var result = await orch.RunAsync(CancellationToken.None);

        if (!result.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false.\n");
        else if (result.PushedToShopify != 1)
            Console.WriteLine($"  FAIL — Expected PushedToShopify=1, got {result.PushedToShopify}\n");
        else
            Console.WriteLine($"  PASS — COMPARE_QUANTITY_STALE retried unconditionally, item pushed.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb14, seeded.Id, origSnapshot!);
    }
}

// -------------------------------------------------------------------------
// TEST 15b: Shopify not-found error — item pushed but Shopify rejects it
//   Verifies: NotFoundInShopify recorded in errors, PushedToShopify=0,
//             LastKnownQty NOT updated (error path must not persist qty)
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 15b] Shopify not-found error — item skipped, error recorded...");
{
    await using var scope15b = syncProvider.CreateAsyncScope();
    var syncDb15b = scope15b.ServiceProvider.GetRequiredService<SyncDbContext>();

    var (seeded, origSnapshot) = await SeedSyncMapRowAsync(syncDb15b, db);
    if (seeded is null)
    {
        Console.WriteLine("  SKIP — no active PCA items.\n");
        goto test15;
    }

    try
    {
        var queryJson15b = ShopifyInventoryQueryResponse(1001, (int)seeded.LastKnownQty);
        var orch = BuildOrchestrator(syncDb15b, new BidirectionalFakeHandler(queryJson15b, ShopifyNotFound()));
        var originalQty15b = seeded.LastKnownQty;
        var result = await orch.RunAsync(CancellationToken.None);
        var notFoundErrors = result.Errors.Where(e => e.Category == SyncErrorCategory.NotFoundInShopify).ToList();
        var refreshed = await syncDb15b.ProductSyncMap
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == seeded.Id);

        if (!result.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false (should succeed even with item-level errors).\n");
        else if (notFoundErrors.Count != 1)
            Console.WriteLine($"  FAIL — Expected 1 NotFoundInShopify error, got {notFoundErrors.Count}\n");
        else if (result.PushedToShopify != 0)
            Console.WriteLine($"  FAIL — Expected PushedToShopify=0, got {result.PushedToShopify}\n");
        else if (refreshed?.LastKnownQty != originalQty15b)
            Console.WriteLine("  FAIL — LastKnownQty was updated despite Shopify error.\n");
        else
            Console.WriteLine($"  PASS — Error recorded, LastKnownQty unchanged, item skipped correctly.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb15b, seeded.Id, origSnapshot!);
    }
}

test15:
// -------------------------------------------------------------------------
// TEST 15: Slow Shopify response — verifies cancellation token is respected
//   Simulates a 4-second Shopify delay; cancels after 1 second.
//   Verifies: RunAsync returns a failed SyncResult (not a hang or unhandled exception)
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 15] Slow Shopify response — cancellation token respected...");
{
    await using var scope15 = syncProvider.CreateAsyncScope();
    var syncDb15 = scope15.ServiceProvider.GetRequiredService<SyncDbContext>();

    var (seeded, origSnapshot) = await SeedSyncMapRowAsync(syncDb15, db);
    if (seeded is null)
    {
        Console.WriteLine("  SKIP — no active PCA items.\n");
        goto syncJobDone;
    }

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // 4-second delay — should be cancelled before it responds.
        // SyncOrchestrator propagates OperationCanceledException — caught here and verified.
        var orch = BuildOrchestrator(syncDb15, FakeShopifyWithDelay(ShopifySuccess(), delayMs: 4_000));
        SyncResult result;
        try
        {
            result = await orch.RunAsync(cts.Token);
            // If RunAsync returned without throwing, it must have been cancelled before hitting Shopify
            if (result.Success)
            {
                Console.WriteLine("  FAIL — Expected cancellation but got Success result.\n");
                goto test15cleanup;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  PASS — OperationCanceledException propagated cleanly (cancellation respected).\n");
            goto test15cleanup;
        }
        Console.WriteLine($"  PASS — Cancelled cleanly. FatalError: \"{result.FatalError}\"\n");
        test15cleanup:;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Unhandled exception (should have been caught): {ex.GetType().Name}: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb15, seeded.Id, origSnapshot!);
    }
}

// -------------------------------------------------------------------------
// TEST 17: Shopify-only delta — Shopify qty differs, PCA unchanged
//   Verifies: PCA In_Stock updated with Shopify delta (when enabled)
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 17] Shopify-only delta — Shopify change written to PCA...");
{
    await using var scope17 = syncProvider.CreateAsyncScope();
    var syncDb17 = scope17.ServiceProvider.GetRequiredService<SyncDbContext>();

    var item = await db.Inventory
        .Where(x => !x.IsDeleted && x.InStock > 1)
        .OrderBy(x => x.ItemNum)
        .FirstOrDefaultAsync();

    if (item is null)
    {
        Console.WriteLine("  SKIP — no active PCA items with stock > 1.\n");
        goto test18;
    }

    var row = await syncDb17.ProductSyncMap
        .FirstOrDefaultAsync(m => m.PcaItemNum == item.ItemNum);
    if (row is null)
    {
        Console.WriteLine("  SKIP — no sync map row.\n");
        goto test18;
    }

    var snap17 = new SyncMapSnapshot(
        row.ShopifyInventoryItemId, row.ShopifyLocationId,
        row.LastKnownQty, row.LastKnownPcaQty, row.LastKnownShopifyQty, row.LastSyncedAt);

    var pcaQty = (int)Math.Max(0, Math.Truncate(item.InStock));
    var shopifyQty = pcaQty - 1; // Simulate Shopify sold 1 unit

    // Set LastKnownQty to match PCA — no PCA delta, only Shopify delta
    row.LastKnownQty = pcaQty;
    row.LastKnownPcaQty = pcaQty;
    row.LastKnownShopifyQty = pcaQty;
    row.LastSyncedAt = DateTime.UtcNow.AddDays(-1);
    await syncDb17.SaveChangesAsync();

    var testConfig17 = new ConfigurationBuilder()
        .AddConfiguration(config)
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BidirectionalSync:ShopifyToPcaEnabled"] = "true"
        })
        .Build();

    try
    {
        var queryJson = ShopifyInventoryQueryResponse(row.ShopifyInventoryItemId, shopifyQty);
        var handler = new BidirectionalFakeHandler(queryJson, ShopifySuccess());
        var pcaWriteDb = BuildTestPcaWriteDb();

        var orch = new SyncOrchestrator(
            db, pcaWriteDb, syncDb17,
            new SyncJob.Shopify.ShopifyClient(new HttpClient(handler)
            {
                BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
                Timeout = Timeout.InfiniteTimeSpan,
            }),
            testConfig17,
            loggerFactory.CreateLogger<SyncOrchestrator>());

        var result = await orch.RunAsync(CancellationToken.None);

        var pcaAfter = await pcaWriteDb.Inventory
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ItemNum == item.ItemNum);

        if (!result.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false. FatalError: {result.FatalError}\n");
        else if (result.PulledFromShopify != 1)
            Console.WriteLine($"  FAIL — Expected PulledFromShopify=1, got {result.PulledFromShopify}\n");
        else if (pcaAfter?.InStock != shopifyQty)
            Console.WriteLine($"  FAIL — PCA In_Stock expected {shopifyQty}, got {pcaAfter?.InStock}\n");
        else
            Console.WriteLine($"  PASS — Shopify delta applied to PCA. In_Stock: {pcaQty} → {shopifyQty}\n");

        // Restore original PCA value
        var restore = await pcaWriteDb.Inventory.FindAsync(item.ItemNum);
        if (restore is not null)
        {
            restore.InStock = item.InStock;
            await pcaWriteDb.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb17, row.Id, snap17);
    }
}

// -------------------------------------------------------------------------
// TEST 17b: ShopifyToPcaEnabled=false — Shopify delta detected but skipped
//   Verifies: PCA In_Stock NOT changed, PulledFromShopify=0
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 17b] ShopifyToPcaEnabled=false — detection only, no PCA write...");
{
    await using var scope17b = syncProvider.CreateAsyncScope();
    var syncDb17b = scope17b.ServiceProvider.GetRequiredService<SyncDbContext>();

    var item17b = await db.Inventory
        .Where(x => !x.IsDeleted && x.InStock > 1)
        .OrderBy(x => x.ItemNum)
        .FirstOrDefaultAsync();

    if (item17b is null)
    {
        Console.WriteLine("  SKIP — no active PCA items with stock > 1.\n");
        goto test18;
    }

    var row17b = await syncDb17b.ProductSyncMap
        .FirstOrDefaultAsync(m => m.PcaItemNum == item17b.ItemNum);
    if (row17b is null)
    {
        Console.WriteLine("  SKIP — no sync map row.\n");
        goto test18;
    }

    var snap17b = new SyncMapSnapshot(
        row17b.ShopifyInventoryItemId, row17b.ShopifyLocationId,
        row17b.LastKnownQty, row17b.LastKnownPcaQty, row17b.LastKnownShopifyQty, row17b.LastSyncedAt);

    var pcaQty17b = (int)Math.Max(0, Math.Truncate(item17b.InStock));
    var shopifyQty17b = pcaQty17b - 1;

    row17b.LastKnownQty = pcaQty17b;
    row17b.LastKnownPcaQty = pcaQty17b;
    row17b.LastKnownShopifyQty = pcaQty17b;
    row17b.LastSyncedAt = DateTime.UtcNow.AddDays(-1);
    await syncDb17b.SaveChangesAsync();

    // ShopifyToPcaEnabled defaults to false in appsettings.json — use base config
    try
    {
        var queryJson17b = ShopifyInventoryQueryResponse(row17b.ShopifyInventoryItemId, shopifyQty17b);
        var handler17b = new BidirectionalFakeHandler(queryJson17b, ShopifySuccess());
        var pcaWriteDb17b = BuildTestPcaWriteDb();

        var orch17b = new SyncOrchestrator(
            db, pcaWriteDb17b, syncDb17b,
            new SyncJob.Shopify.ShopifyClient(new HttpClient(handler17b)
            {
                BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
                Timeout = Timeout.InfiniteTimeSpan,
            }),
            config, // ShopifyToPcaEnabled=false (default)
            loggerFactory.CreateLogger<SyncOrchestrator>());

        var result17b = await orch17b.RunAsync(CancellationToken.None);

        var pcaAfter17b = await pcaWriteDb17b.Inventory
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ItemNum == item17b.ItemNum);

        if (!result17b.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false. FatalError: {result17b.FatalError}\n");
        else if (result17b.PulledFromShopify != 0)
            Console.WriteLine($"  FAIL — Expected PulledFromShopify=0 (disabled), got {result17b.PulledFromShopify}\n");
        else if (pcaAfter17b?.InStock != item17b.InStock)
            Console.WriteLine($"  FAIL — PCA In_Stock should be unchanged ({item17b.InStock}), got {pcaAfter17b?.InStock}\n");
        else
            Console.WriteLine($"  PASS — Shopify delta detected but skipped. PCA unchanged at {item17b.InStock}.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb17b, row17b.Id, snap17b);
    }
}

test18:
// -------------------------------------------------------------------------
// TEST 18: Conflict — both sides changed, PCA wins
//   Verifies: Shopify gets PCA value, PCA unchanged, conflict logged
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 18] Conflict — both sides changed, PCA wins...");
{
    await using var scope18 = syncProvider.CreateAsyncScope();
    var syncDb18 = scope18.ServiceProvider.GetRequiredService<SyncDbContext>();

    var item = await db.Inventory
        .Where(x => !x.IsDeleted && x.InStock > 2)
        .OrderBy(x => x.ItemNum)
        .FirstOrDefaultAsync();

    if (item is null)
    {
        Console.WriteLine("  SKIP — no active PCA items with stock > 2.\n");
        goto test19;
    }

    var row = await syncDb18.ProductSyncMap
        .FirstOrDefaultAsync(m => m.PcaItemNum == item.ItemNum);
    if (row is null)
    {
        Console.WriteLine("  SKIP — no sync map row.\n");
        goto test19;
    }

    var snap18 = new SyncMapSnapshot(
        row.ShopifyInventoryItemId, row.ShopifyLocationId,
        row.LastKnownQty, row.LastKnownPcaQty, row.LastKnownShopifyQty, row.LastSyncedAt);

    var pcaQty = (int)Math.Max(0, Math.Truncate(item.InStock));
    var lastKnown = pcaQty + 5; // PCA delta = pcaQty - (pcaQty+5) = -5
    var shopifyQty = lastKnown - 2; // Shopify delta = -2

    row.LastKnownQty = lastKnown;
    row.LastKnownPcaQty = lastKnown;
    row.LastKnownShopifyQty = lastKnown;
    row.LastSyncedAt = DateTime.UtcNow.AddDays(-1);
    await syncDb18.SaveChangesAsync();

    var testConfig18 = new ConfigurationBuilder()
        .AddConfiguration(config)
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BidirectionalSync:ShopifyToPcaEnabled"] = "true"
        })
        .Build();

    try
    {
        var queryJson = ShopifyInventoryQueryResponse(row.ShopifyInventoryItemId, shopifyQty);
        var handler = new BidirectionalFakeHandler(queryJson, ShopifySuccess());
        var pcaWriteDb = BuildTestPcaWriteDb();

        var orch = new SyncOrchestrator(
            db, pcaWriteDb, syncDb18,
            new SyncJob.Shopify.ShopifyClient(new HttpClient(handler)
            {
                BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
                Timeout = Timeout.InfiniteTimeSpan,
            }),
            testConfig18,
            loggerFactory.CreateLogger<SyncOrchestrator>());

        var result = await orch.RunAsync(CancellationToken.None);

        // Verify PCA value was NOT changed (PCA won → Shopify gets overwritten, PCA stays)
        var pcaAfter = await pcaWriteDb.Inventory
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ItemNum == item.ItemNum);

        if (!result.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false. FatalError: {result.FatalError}\n");
        else if (result.PushedToShopify != 1)
            Console.WriteLine($"  FAIL — Expected PushedToShopify=1, got {result.PushedToShopify}\n");
        else if (result.ConflictsPcaWon != 1)
            Console.WriteLine($"  FAIL — Expected ConflictsPcaWon=1, got {result.ConflictsPcaWon}\n");
        else if (pcaAfter?.InStock != item.InStock)
            Console.WriteLine($"  FAIL — PCA In_Stock should be unchanged ({item.InStock}), got {pcaAfter?.InStock}\n");
        else
            Console.WriteLine($"  PASS — Conflict detected, PCA won. Shopify pushed to {pcaQty}, PCA unchanged at {item.InStock}.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb18, row.Id, snap18);
    }
}

test19:
// -------------------------------------------------------------------------
// TEST 19: Shopify query failure — sync cycle aborts
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 19] Shopify query failure — sync cycle aborts...");
{
    await using var scope19 = syncProvider.CreateAsyncScope();
    var syncDb19 = scope19.ServiceProvider.GetRequiredService<SyncDbContext>();

    var (seeded, origSnapshot) = await SeedSyncMapRowAsync(syncDb19, db);
    if (seeded is null)
    {
        Console.WriteLine("  SKIP — no active PCA items.\n");
        goto test20;
    }

    try
    {
        // Return HTTP 200 with a broken GraphQL response (missing 'data' key).
        // Avoids the 3-retry × 5s delay that HTTP 500 would trigger.
        var brokenResponse = """{"errors": [{"message": "Internal error"}]}""";
        var handler = new FakeHttpHandler(brokenResponse);
        var pcaWriteDb = BuildTestPcaWriteDb();

        var orch = new SyncOrchestrator(
            db, pcaWriteDb, syncDb19,
            new SyncJob.Shopify.ShopifyClient(new HttpClient(handler)
            {
                BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
                Timeout = Timeout.InfiniteTimeSpan,
            }),
            config,
            loggerFactory.CreateLogger<SyncOrchestrator>());

        var result = await orch.RunAsync(CancellationToken.None);

        if (result.Success)
            Console.WriteLine("  FAIL — Expected Success=false when Shopify query fails.\n");
        else if (result.FatalError is null || !result.FatalError.Contains("Shopify"))
            Console.WriteLine($"  FAIL — Expected FatalError mentioning Shopify, got: {result.FatalError}\n");
        else
            Console.WriteLine($"  PASS — Sync aborted. FatalError: {result.FatalError}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb19, seeded.Id, origSnapshot!);
    }
}

test20:
// -------------------------------------------------------------------------
// TEST 20: PCA write failure — item logged as PcaWriteFailed
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 20] PCA write failure — error recorded, other items unaffected...");
{
    await using var scope20 = syncProvider.CreateAsyncScope();
    var syncDb20 = scope20.ServiceProvider.GetRequiredService<SyncDbContext>();

    var item = await db.Inventory
        .Where(x => !x.IsDeleted && x.InStock > 1)
        .OrderBy(x => x.ItemNum)
        .FirstOrDefaultAsync();

    if (item is null)
    {
        Console.WriteLine("  SKIP — no active PCA items with stock > 1.\n");
        goto test21;
    }

    var row = await syncDb20.ProductSyncMap
        .FirstOrDefaultAsync(m => m.PcaItemNum == item.ItemNum);
    if (row is null)
    {
        Console.WriteLine("  SKIP — no sync map row.\n");
        goto test21;
    }

    var snap20 = new SyncMapSnapshot(
        row.ShopifyInventoryItemId, row.ShopifyLocationId,
        row.LastKnownQty, row.LastKnownPcaQty, row.LastKnownShopifyQty, row.LastSyncedAt);

    var pcaQty = (int)Math.Max(0, Math.Truncate(item.InStock));
    var shopifyQty = pcaQty - 1;

    // Set LastKnownQty to match PCA — only Shopify delta
    row.LastKnownQty = pcaQty;
    row.LastKnownPcaQty = pcaQty;
    row.LastKnownShopifyQty = pcaQty;
    row.LastSyncedAt = DateTime.UtcNow.AddDays(-1);
    await syncDb20.SaveChangesAsync();

    var testConfig20 = new ConfigurationBuilder()
        .AddConfiguration(config)
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BidirectionalSync:ShopifyToPcaEnabled"] = "true"
        })
        .Build();

    try
    {
        var queryJson = ShopifyInventoryQueryResponse(row.ShopifyInventoryItemId, shopifyQty);
        var handler = new BidirectionalFakeHandler(queryJson, ShopifySuccess());

        // Use a broken PcaWriteDbContext (bad connection string)
        var badOpts = new DbContextOptionsBuilder<PcaWriteDbContext>()
            .UseSqlServer("Server=NONEXISTENT;Database=FAKE;Connection Timeout=1")
            .Options;
        var badPcaWriteDb = new PcaWriteDbContext(badOpts);

        var orch = new SyncOrchestrator(
            db, badPcaWriteDb, syncDb20,
            new SyncJob.Shopify.ShopifyClient(new HttpClient(handler)
            {
                BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
                Timeout = Timeout.InfiniteTimeSpan,
            }),
            testConfig20,
            loggerFactory.CreateLogger<SyncOrchestrator>());

        var result = await orch.RunAsync(CancellationToken.None);
        var pcaWriteErrors = result.Errors
            .Where(e => e.Category == SyncErrorCategory.PcaWriteFailed)
            .ToList();

        if (!result.Success)
            Console.WriteLine($"  FAIL — Expected Success=true (item-level failure, not fatal). FatalError: {result.FatalError}\n");
        else if (result.PulledFromShopify != 0)
            Console.WriteLine($"  FAIL — Expected PulledFromShopify=0 (write failed), got {result.PulledFromShopify}\n");
        else if (pcaWriteErrors.Count != 1)
            Console.WriteLine($"  FAIL — Expected 1 PcaWriteFailed error, got {pcaWriteErrors.Count}\n");
        else
            Console.WriteLine($"  PASS — PCA write failure recorded, sync still succeeded. Error: {pcaWriteErrors[0].Detail?[..Math.Min(80, pcaWriteErrors[0].Detail?.Length ?? 0)]}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb20, row.Id, snap20);
    }
}

test21:
// -------------------------------------------------------------------------
// TEST 21: No false delta after Shopify→PCA write
//   Run two consecutive syncs. First detects Shopify delta and writes to PCA.
//   Second should detect no changes.
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 21] No false delta after Shopify→PCA write...");
{
    await using var scope21 = syncProvider.CreateAsyncScope();
    var syncDb21 = scope21.ServiceProvider.GetRequiredService<SyncDbContext>();

    var item = await db.Inventory
        .Where(x => !x.IsDeleted && x.InStock > 2)
        .OrderBy(x => x.ItemNum)
        .FirstOrDefaultAsync();

    if (item is null)
    {
        Console.WriteLine("  SKIP — no active PCA items with stock > 2.\n");
        goto syncJobDone;
    }

    var row = await syncDb21.ProductSyncMap
        .FirstOrDefaultAsync(m => m.PcaItemNum == item.ItemNum);
    if (row is null)
    {
        Console.WriteLine("  SKIP — no sync map row.\n");
        goto syncJobDone;
    }

    var snap21 = new SyncMapSnapshot(
        row.ShopifyInventoryItemId, row.ShopifyLocationId,
        row.LastKnownQty, row.LastKnownPcaQty, row.LastKnownShopifyQty, row.LastSyncedAt);

    var pcaQty = (int)Math.Max(0, Math.Truncate(item.InStock));
    var shopifyQty = pcaQty - 1;

    // Set LastKnownQty to match PCA — only Shopify delta
    row.LastKnownQty = pcaQty;
    row.LastKnownPcaQty = pcaQty;
    row.LastKnownShopifyQty = pcaQty;
    row.LastSyncedAt = DateTime.UtcNow.AddDays(-1);
    await syncDb21.SaveChangesAsync();

    var testConfig21 = new ConfigurationBuilder()
        .AddConfiguration(config)
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BidirectionalSync:ShopifyToPcaEnabled"] = "true"
        })
        .Build();

    try
    {
        var pcaWriteDb = BuildTestPcaWriteDb();

        // Run 1: Shopify at shopifyQty, PCA at pcaQty → Shopify→PCA write
        var queryJson1 = ShopifyInventoryQueryResponse(row.ShopifyInventoryItemId, shopifyQty);
        var handler1 = new BidirectionalFakeHandler(queryJson1, ShopifySuccess());
        var orch1 = new SyncOrchestrator(
            db, pcaWriteDb, syncDb21,
            new SyncJob.Shopify.ShopifyClient(new HttpClient(handler1)
            {
                BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
                Timeout = Timeout.InfiniteTimeSpan,
            }),
            testConfig21,
            loggerFactory.CreateLogger<SyncOrchestrator>());

        var result1 = await orch1.RunAsync(CancellationToken.None);

        if (result1.PulledFromShopify != 1)
        {
            Console.WriteLine($"  FAIL — Run 1: Expected PulledFromShopify=1, got {result1.PulledFromShopify}\n");
        }
        else
        {
            // Run 2: Both sides now at shopifyQty → no delta
            await using var scope21b = syncProvider.CreateAsyncScope();
            var syncDb21b = scope21b.ServiceProvider.GetRequiredService<SyncDbContext>();
            var pcaWriteDb2 = BuildTestPcaWriteDb();

            var queryJson2 = ShopifyInventoryQueryResponse(row.ShopifyInventoryItemId, shopifyQty);
            var handler2 = new BidirectionalFakeHandler(queryJson2, ShopifySuccess());
            var orch2 = new SyncOrchestrator(
                db, pcaWriteDb2, syncDb21b,
                new SyncJob.Shopify.ShopifyClient(new HttpClient(handler2)
                {
                    BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
                    Timeout = Timeout.InfiniteTimeSpan,
                }),
                testConfig21,
                loggerFactory.CreateLogger<SyncOrchestrator>());

            var result2 = await orch2.RunAsync(CancellationToken.None);

            if (result2.ChangedItems != 0)
                Console.WriteLine($"  FAIL — Run 2: Expected ChangedItems=0 (no false delta), got {result2.ChangedItems}\n");
            else
                Console.WriteLine($"  PASS — No false delta on second run. Run 1 pulled 1 from Shopify, Run 2 detected 0 changes.\n");
        }

        // Restore original PCA value
        var restore = await pcaWriteDb.Inventory.FindAsync(item.ItemNum);
        if (restore is not null)
        {
            restore.InStock = item.InStock;
            await pcaWriteDb.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await RestoreSyncMapRowAsync(syncDb21, row.Id, snap21);
    }
}

syncJobDone:
Console.WriteLine("=== SyncJob tests complete ===");

// =========================================================================
// Fake HTTP handlers (defined after top-level statements)
// =========================================================================

/// <summary>
/// Returns the same JSON body for every request.
/// Ignores the HttpClient-linked CancellationToken — use CancellableFakeHandler for cancellation tests.
/// </summary>
sealed class FakeHttpHandler(string responseJson) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
}

/// <summary>
/// Delays for delayMs before responding, respecting the caller's CancellationToken.
/// Used only for cancellation tests where the delay must be interruptible.
/// </summary>
sealed class CancellableFakeHandler(string responseJson, int delayMs) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>Returns responses from a fixed sequence, one per call.</summary>
sealed class SequentialFakeHandler(IReadOnlyList<string> responses) : HttpMessageHandler
{
    private int _index;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var json = responses[Math.Min(_index++, responses.Count - 1)];
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Returns different responses for inventory query vs mutation requests.
/// Inspects the request body to determine which response to return.
/// </summary>
sealed class BidirectionalFakeHandler(string queryResponse, string mutationResponse) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        var json = body.Contains("queryInventoryLevels") ? queryResponse : mutationResponse;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

sealed class BidirectionalSequentialHandler(string queryResponse, IReadOnlyList<string> mutationResponses) : HttpMessageHandler
{
    private int _mutationIndex;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        string json;
        if (body.Contains("queryInventoryLevels"))
            json = queryResponse;
        else
            json = mutationResponses[Math.Min(_mutationIndex++, mutationResponses.Count - 1)];
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

record SyncMapSnapshot(
    long ShopifyInventoryItemId, long ShopifyLocationId,
    decimal LastKnownQty, decimal LastKnownPcaQty, decimal LastKnownShopifyQty, DateTime LastSyncedAt);
