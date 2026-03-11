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

// Helper: builds a fake HttpMessageHandler that returns a fixed JSON body.
// Optionally delays the response to simulate slow Shopify API.
static HttpMessageHandler FakeShopify(string responseJson) =>
    new FakeHttpHandler(responseJson);

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
async Task<(ProductSyncMap? row, string? pcaItemNum)> SeedSyncMapRowAsync(SyncDbContext db, PcAmericaDbContext pca)
{
    var item = await pca.Inventory
        .Where(x => !x.IsDeleted && x.InStock > 0)
        .OrderBy(x => x.ItemNum)
        .FirstOrDefaultAsync();

    if (item is null) return (null, null);

    // Use a LastKnownQty that differs from InStock so it's always "changed"
    var differentQty = item.InStock + 99;

    var row = new ProductSyncMap
    {
        PcaItemNum             = item.ItemNum,
        PcaUpc                 = null,
        ShopifyProductId       = 1,
        ShopifyVariantId       = 2,
        ShopifyInventoryItemId = 1001,
        ShopifyLocationId      = 88892145881L,
        LastKnownQty           = differentQty,
        LastSyncedAt           = DateTime.UtcNow.AddDays(-1),
    };
    db.ProductSyncMap.Add(row);
    await db.SaveChangesAsync();
    return (row, item.ItemNum);
}

async Task CleanupSyncMapRowAsync(SyncDbContext db, int id)
{
    var row = await db.ProductSyncMap.FindAsync(id);
    if (row is not null)
    {
        db.ProductSyncMap.Remove(row);
        await db.SaveChangesAsync();
    }
}

// Re-use the existing DB contexts from the SyncData tests above.
// Build a minimal logging factory (suppresses noise in test output).
var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

SyncOrchestrator BuildOrchestrator(SyncDbContext syncDbCtx, HttpMessageHandler handler)
{
    // Use the test constructor — bypasses Shopify config parsing, fake handler is injected directly.
    var http = new HttpClient(handler)
    {
        BaseAddress = new Uri("https://fake.myshopify.com/admin/api/2025-10/"),
        Timeout = Timeout.InfiniteTimeSpan, // let CancellationToken control cancellation, not HttpClient timeout
    };
    var shopifyClient = new SyncJob.Shopify.ShopifyClient(http);
    return new SyncOrchestrator(
        db,
        syncDbCtx,
        shopifyClient,
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

    var (seeded, _) = await SeedSyncMapRowAsync(syncDb11, db);
    if (seeded is null)
    {
        Console.WriteLine("  SKIP — no active PCA items with positive stock.\n");
        goto test12;
    }

    try
    {
        var orch = BuildOrchestrator(syncDb11, FakeShopify(ShopifySuccess()));
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
        await CleanupSyncMapRowAsync(syncDb11, seeded.Id);
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

    var row = new ProductSyncMap
    {
        PcaItemNum             = item.ItemNum,
        ShopifyProductId       = 1,
        ShopifyVariantId       = 2,
        ShopifyInventoryItemId = 1002,
        ShopifyLocationId      = 88892145881L,
        LastKnownQty           = item.InStock,  // matches exactly — should not push
        LastSyncedAt           = DateTime.UtcNow,
    };
    syncDb12.ProductSyncMap.Add(row);
    await syncDb12.SaveChangesAsync();

    try
    {
        // Handler that throws if called — it must NOT be called for this test
        var orch = BuildOrchestrator(syncDb12, FakeShopify(ShopifySuccess()));
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
        await CleanupSyncMapRowAsync(syncDb12, row.Id);
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
        var orch = BuildOrchestrator(syncDb13, FakeShopify(ShopifySuccess()));
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
//   Verifies: ConflictOverwritten in errors, item still counted as pushed
// -------------------------------------------------------------------------
Console.WriteLine("[TEST 14] Conflict retry path — COMPARE_QUANTITY_STALE → retry succeeds...");
{
    await using var scope14 = syncProvider.CreateAsyncScope();
    var syncDb14 = scope14.ServiceProvider.GetRequiredService<SyncDbContext>();

    var (seeded, _) = await SeedSyncMapRowAsync(syncDb14, db);
    if (seeded is null)
    {
        Console.WriteLine("  SKIP — no active PCA items.\n");
        goto test15;
    }

    try
    {
        // First call returns STALE, second call (unconditional retry) returns success
        var handler = new SequentialFakeHandler([ShopifyStale(), ShopifySuccess()]);
        var orch = BuildOrchestrator(syncDb14, handler);
        var result = await orch.RunAsync(CancellationToken.None);

        var conflictErrors = result.Errors.Where(e => e.Category == SyncErrorCategory.ConflictOverwritten).ToList();

        if (!result.Success)
            Console.WriteLine($"  FAIL — SyncResult.Success is false.\n");
        else if (result.PushedToShopify != 1)
            Console.WriteLine($"  FAIL — Expected PushedToShopify=1, got {result.PushedToShopify}\n");
        else if (conflictErrors.Count != 1)
            Console.WriteLine($"  FAIL — Expected 1 ConflictOverwritten error, got {conflictErrors.Count}\n");
        else
            Console.WriteLine($"  PASS — Conflict retried, item pushed, ConflictOverwritten logged.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — Exception: {ex.Message}\n");
    }
    finally
    {
        await CleanupSyncMapRowAsync(syncDb14, seeded.Id);
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

    var (seeded, _) = await SeedSyncMapRowAsync(syncDb15b, db);
    if (seeded is null)
    {
        Console.WriteLine("  SKIP — no active PCA items.\n");
        goto test15;
    }

    try
    {
        var orch = BuildOrchestrator(syncDb15b, FakeShopify(ShopifyNotFound()));
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
        await CleanupSyncMapRowAsync(syncDb15b, seeded.Id);
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

    var (seeded, _) = await SeedSyncMapRowAsync(syncDb15, db);
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
        await CleanupSyncMapRowAsync(syncDb15, seeded.Id);
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
