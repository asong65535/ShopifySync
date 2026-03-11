using BootstrapJob.Shopify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PcaData;
using SyncData;

namespace BootstrapJob.Bootstrap;

internal sealed class BootstrapOrchestrator
{
    private readonly PcAmericaDbContext _pcaDb;
    private readonly SyncDbContext _syncDb;
    private readonly ShopifyClient _shopify;
    private readonly JsonlBuilder _jsonlBuilder;
    private readonly SyncMapWriter _syncMapWriter;
    private readonly IConfiguration _config;

    public BootstrapOrchestrator(
        PcAmericaDbContext pcaDb,
        SyncDbContext syncDb,
        ShopifyClient shopify,
        JsonlBuilder jsonlBuilder,
        SyncMapWriter syncMapWriter,
        IConfiguration config)
    {
        _pcaDb = pcaDb;
        _syncDb = syncDb;
        _shopify = shopify;
        _jsonlBuilder = jsonlBuilder;
        _syncMapWriter = syncMapWriter;
        _config = config;
    }

    public async Task RunAsync(bool dryRun, CancellationToken ct = default)
    {
        if (dryRun)
            Console.WriteLine("*** DRY RUN MODE — no writes to Shopify or SQL ***\n");

        // ------------------------------------------------------------------
        // Pre-flight checks
        // ------------------------------------------------------------------
        var locationId = _config.GetValue<long>("Bootstrap:LocationId");
        var pollTimeoutMinutes = _config.GetValue<int>("Bootstrap:PollTimeoutMinutes", 30);

        if (locationId == 0)
        {
            var locations = await _shopify.FetchLocationsAsync(ct);
            if (locations.Count == 0)
                throw new InvalidOperationException("No locations found in Shopify store.");

            if (locations.Count == 1)
            {
                locationId = locations[0].Id;
                Console.WriteLine($"      Auto-selected location id={locationId}\n");
            }
            else
            {
                // Multiple locations — require explicit config
                Console.WriteLine("Multiple locations found — set Bootstrap:LocationId in appsettings.local.json:");
                foreach (var loc in locations)
                    Console.WriteLine($"  {loc.Id}");
                return;
            }
        }

        // Guard: if ProductSyncMap already has rows, require --force
        var existingCount = await _syncDb.ProductSyncMap.CountAsync(ct);
        if (existingCount > 0)
        {
            Console.WriteLine($"ERROR — ProductSyncMap already contains {existingCount:N0} rows.");
            Console.WriteLine("Re-run with --force to truncate and re-import.");
            return;
        }

        // ------------------------------------------------------------------
        // STEP 1: Read PCA items
        // ------------------------------------------------------------------
        Console.WriteLine("[1/5] Reading PCA inventory...");
        var items = await _pcaDb.Inventory
            .Where(x => !x.IsDeleted)
            .Include(x => x.Skus)
            .ToListAsync(ct);
        Console.WriteLine($"      {items.Count:N0} active items loaded.\n");

        // ------------------------------------------------------------------
        // STEP 2: Build JSONL
        // ------------------------------------------------------------------
        Console.WriteLine("[2/5] Building JSONL...");
        var (jsonlStream, written, negativeClamped) = _jsonlBuilder.Build(items, locationId);
        Console.WriteLine($"      {written:N0} lines written, {negativeClamped} negative-stock values clamped to 0.\n");

        if (dryRun)
        {
            Console.WriteLine("Dry run complete — JSONL built but not uploaded.");
            return;
        }

        // ------------------------------------------------------------------
        // STEP 3: Stage upload + PUT
        // ------------------------------------------------------------------
        Console.WriteLine("[3/5] Creating staged upload...");
        var target = await _shopify.CreateStagedUploadAsync(ct);
        Console.WriteLine($"      Upload URL: {target.Url[..Math.Min(60, target.Url.Length)]}...");

        Console.WriteLine("      Uploading JSONL...");
        await _shopify.UploadFileAsync(target.Url, jsonlStream, ct);
        Console.WriteLine("      Upload complete.\n");

        // Extract the path component from the resourceUrl for the bulk op call
        var resourceUri = new Uri(target.ResourceUrl);
        var stagedUploadPath = resourceUri.AbsolutePath.TrimStart('/');

        // ------------------------------------------------------------------
        // STEP 4: Trigger bulk operation
        // ------------------------------------------------------------------
        Console.WriteLine("[4/5] Triggering bulk operation...");
        var bulkOpId = await _shopify.RunBulkOperationAsync(stagedUploadPath, ct);
        Console.WriteLine($"      Bulk operation ID: {bulkOpId}\n");

        // ------------------------------------------------------------------
        // STEP 5: Poll until complete, then write ProductSyncMap
        // ------------------------------------------------------------------
        Console.WriteLine("[5/5] Polling bulk operation status...");
        var timeout = TimeSpan.FromMinutes(pollTimeoutMinutes);
        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromSeconds(3);

        string? resultUrl = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollInterval, ct);
            var status = await _shopify.PollBulkOperationAsync(bulkOpId, ct);

            if (status is null)
            {
                Console.WriteLine("      Status: pending...");
                continue;
            }

            Console.WriteLine($"      Status: {status.Status}  Objects: {status.ObjectCount:N0}");

            switch (status.Status)
            {
                case "COMPLETED":
                    resultUrl = status.Url;
                    Console.WriteLine($"      Completed. {status.ObjectCount:N0} objects processed.\n");
                    goto done;

                case "FAILED":
                case "CANCELED":
                case "CANCELING":
                    throw new ShopifyApiException(
                        $"Bulk operation ended with status '{status.Status}'. ErrorCode: {status.ErrorCode}");
            }
        }

        throw new TimeoutException(
            $"Bulk operation did not complete within {pollTimeoutMinutes} minutes.");

        done:
        if (resultUrl is null)
            throw new ShopifyApiException("Bulk operation completed but returned no result URL.");

        Console.WriteLine("      Downloading result JSONL...");
        var resultStream = await _shopify.DownloadResultAsync(resultUrl, ct);

        Console.WriteLine("      Writing ProductSyncMap...");
        var (mapWritten, mapSkipped) = await _syncMapWriter.WriteAsync(resultStream, items, locationId, ct);

        Console.WriteLine($"\n=== Bootstrap complete ===");
        Console.WriteLine($"  PCA items read   : {items.Count:N0}");
        Console.WriteLine($"  JSONL lines      : {written:N0}");
        Console.WriteLine($"  Negative clamped : {negativeClamped}");
        Console.WriteLine($"  SyncMap written  : {mapWritten:N0}");
        Console.WriteLine($"  Skipped/warnings : {mapSkipped}");
    }
}
