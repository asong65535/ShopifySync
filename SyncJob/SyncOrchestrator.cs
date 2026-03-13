using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PcaData;
using SyncData;
using SyncData.Models;
using SyncJob.Shopify;

namespace SyncJob;

/// <summary>
/// Executes one bidirectional delta sync run. Injected into SyncService via scope.
/// </summary>
public sealed class SyncOrchestrator
{
    private const int BatchSize = 250;

    private readonly PcAmericaDbContext _pcaDb;
    private readonly PcaWriteDbContext _pcaWriteDb;
    private readonly SyncDbContext _syncDb;
    private readonly ShopifyClient _shopify;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        PcAmericaDbContext pcaDb,
        PcaWriteDbContext pcaWriteDb,
        SyncDbContext syncDb,
        ShopifyClient shopify,
        IConfiguration config,
        ILogger<SyncOrchestrator> logger)
    {
        _pcaDb = pcaDb;
        _pcaWriteDb = pcaWriteDb;
        _syncDb = syncDb;
        _shopify = shopify;
        _config = config;
        _logger = logger;
    }

    public async Task<SyncResult> RunAsync(CancellationToken ct)
    {
        var errors = new List<SyncItemError>();
        int totalPca = 0, changedItems = 0, pushedToShopify = 0, notInSyncMap = 0;

        // ------------------------------------------------------------------
        // Step 1: Read SyncState — create row if missing so it's always persisted
        // ------------------------------------------------------------------
        var state = await _syncDb.SyncState.FindAsync([1], ct);
        if (state is null)
        {
            state = new SyncState { Id = 1, LastPolledAt = null };
            _syncDb.SyncState.Add(state);
            await _syncDb.SaveChangesAsync(ct);
        }
        _logger.LogInformation("Starting sync run. LastPolledAt={LastPolledAt}", state.LastPolledAt);

        // ------------------------------------------------------------------
        // Step 2: Load all active PCA items
        // ------------------------------------------------------------------
        var pcaItems = await _pcaDb.Inventory
            .Where(x => !x.IsDeleted)
            .ToListAsync(ct);

        totalPca = pcaItems.Count;
        _logger.LogInformation("Loaded {Count} active PCA items.", totalPca);

        // ------------------------------------------------------------------
        // Step 3: Load all ProductSyncMap rows into a lookup
        // ------------------------------------------------------------------
        var syncMap = await _syncDb.ProductSyncMap.ToListAsync(ct);
        var mapByItemNum = syncMap.ToDictionary(m => m.PcaItemNum, StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------
        // Step 3b: Identify and log unmatched items (no sync map row)
        // ------------------------------------------------------------------
        foreach (var item in pcaItems)
        {
            if (!mapByItemNum.ContainsKey(item.ItemNum))
            {
                notInSyncMap++;
                _logger.LogWarning(
                    "PCA item {ItemNum} ({ItemName}) has no ProductSyncMap row — skipping.",
                    item.ItemNum, item.ItemName);
            }
        }

        if (notInSyncMap > 0)
            _logger.LogWarning("{Count} PCA items are not in the sync map (likely new items not yet bootstrapped).", notInSyncMap);

        // ------------------------------------------------------------------
        // Step 3c: Query Shopify inventory for all mapped items
        // ------------------------------------------------------------------
        var shopifyQtyByItemId = new Dictionary<long, int>();
        try
        {
            var groupedByLocation = syncMap
                .GroupBy(m => m.ShopifyLocationId)
                .ToList();

            foreach (var locationGroup in groupedByLocation)
            {
                var locationGid = ShopifyClient.ToGid("Location", locationGroup.Key);
                var gids = locationGroup
                    .Select(m => ShopifyClient.ToGid("InventoryItem", m.ShopifyInventoryItemId))
                    .ToList();

                for (int i = 0; i < gids.Count; i += BatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = gids.Skip(i).Take(BatchSize).ToList();
                    var batchResults = await _shopify.QueryInventoryLevelsAsync(batch, locationGid, ct);

                    foreach (var kvp in batchResults)
                        shopifyQtyByItemId[kvp.Key] = kvp.Value;

                    // Rate limit: 1s delay between batches to preserve Shopify API budget
                    if (i + BatchSize < gids.Count)
                        await Task.Delay(1_000, ct);
                }
            }

            _logger.LogInformation("Queried Shopify inventory for {Count} items.", shopifyQtyByItemId.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to query Shopify inventory — aborting sync cycle.");
            return new SyncResult
            {
                Success = false,
                CompletedAt = DateTime.UtcNow,
                FatalError = $"Shopify inventory query failed: {ex.Message}",
                TotalPcaItems = totalPca,
            };
        }

        // ------------------------------------------------------------------
        // Step 4: Three-way delta comparison
        // ------------------------------------------------------------------
        var pcaToShopify = new List<(PcaData.Models.PcaInventoryItem Item, ProductSyncMap Map, int NewQty)>();
        var shopifyToPca = new List<(ProductSyncMap Map, int NewQty)>();
        var conflicts = new List<(PcaData.Models.PcaInventoryItem Item, ProductSyncMap Map, int NewQty, int ShopifyDelta)>();
        int noChangeCount = 0;

        var shopifyToPcaEnabled = _config.GetValue<bool>("BidirectionalSync:ShopifyToPcaEnabled");

        foreach (var pcaItem in pcaItems)
        {
            if (!mapByItemNum.TryGetValue(pcaItem.ItemNum, out var map))
                continue;

            var pcaQty = (int)Math.Max(0, Math.Truncate(pcaItem.InStock));
            var lastKnown = (int)Math.Max(0, Math.Truncate(map.LastKnownQty));
            var pcaDelta = pcaQty - lastKnown;

            if (!shopifyQtyByItemId.TryGetValue(map.ShopifyInventoryItemId, out var shopifyQty))
            {
                errors.Add(new SyncItemError
                {
                    PcaItemNum = pcaItem.ItemNum,
                    Category = SyncErrorCategory.ShopifyQueryFailed,
                    Detail = $"InventoryItem {map.ShopifyInventoryItemId} not in Shopify query response.",
                });
                shopifyQty = lastKnown;
            }

            var shopifyDelta = shopifyQty - lastKnown;

            if (pcaDelta != 0 && shopifyDelta == 0)
            {
                pcaToShopify.Add((pcaItem, map, pcaQty));
            }
            else if (pcaDelta == 0 && shopifyDelta != 0)
            {
                if (shopifyToPcaEnabled)
                    shopifyToPca.Add((map, shopifyQty));
                else
                    _logger.LogInformation(
                        "Shopify delta detected for {ItemNum} (delta={Delta}) but ShopifyToPcaEnabled=false — skipping.",
                        pcaItem.ItemNum, shopifyDelta);
            }
            else if (pcaDelta != 0 && shopifyDelta != 0)
            {
                conflicts.Add((pcaItem, map, pcaQty, shopifyDelta));
            }
            else
            {
                noChangeCount++;
            }
        }

        changedItems = pcaToShopify.Count + shopifyToPca.Count + conflicts.Count;
        _logger.LogInformation(
            "Delta: {PcaToShopify} PCA→Shopify, {ShopifyToPca} Shopify→PCA, {Conflicts} conflicts, {NoChange} unchanged.",
            pcaToShopify.Count, shopifyToPca.Count, conflicts.Count, noChangeCount);

        if (changedItems == 0)
        {
            await UpdateSyncStateAsync(ct);
            return BuildResult(true, totalPca, 0, 0, 0, 0, notInSyncMap, errors);
        }

        // ------------------------------------------------------------------
        // Step 5: Push PCA→Shopify (includes conflicts where PCA wins)
        // ------------------------------------------------------------------
        var shopifyPushItems = pcaToShopify
            .Select(x => (Item: x.Item, Map: x.Map, NewQty: x.NewQty))
            .Concat(conflicts.Select(x => (Item: x.Item, Map: x.Map, NewQty: x.NewQty)))
            .ToList();

        var successfulShopifyPushes = new List<(ProductSyncMap Map, int NewQty)>();

        for (int i = 0; i < shopifyPushItems.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = shopifyPushItems.Skip(i).Take(BatchSize).ToList();

            var lines = batch.Select(pair =>
            {
                var compareQty = (int)Math.Truncate(pair.Map.LastKnownQty);
                return new InventoryQuantityLine
                {
                    InventoryItemGid = ShopifyClient.ToGid("InventoryItem", pair.Map.ShopifyInventoryItemId),
                    LocationGid = ShopifyClient.ToGid("Location", pair.Map.ShopifyLocationId),
                    Quantity = pair.NewQty,
                    CompareQuantity = compareQty,
                    PcaItemNum = pair.Item.ItemNum,
                };
            }).ToList();

            _logger.LogDebug("Pushing batch {Start}-{End} ({Count} items).", i + 1, i + batch.Count, batch.Count);

            // Pass 1: compare-and-set
            Dictionary<string, InventorySetResult> results;
            try
            {
                results = await _shopify.SetQuantitiesWithCompareAsync(lines, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shopify API call failed for batch starting at index {Start}.", i);
                foreach (var pair in batch)
                    errors.Add(new SyncItemError
                    {
                        PcaItemNum = pair.Item.ItemNum,
                        Category = SyncErrorCategory.NotFoundInShopify,
                        Detail = ex.Message,
                    });
                continue;
            }

            // Separate outcomes
            var batchMapByItemNum = batch.ToDictionary(p => p.Item.ItemNum, p => p.Map, StringComparer.OrdinalIgnoreCase);
            var conflictLines = new List<InventoryQuantityLine>();
            foreach (var line in lines)
            {
                var result = results[line.PcaItemNum];
                if (result.IsSuccess)
                {
                    var map = batchMapByItemNum[line.PcaItemNum];
                    successfulShopifyPushes.Add((map, line.Quantity));
                    pushedToShopify++;
                }
                else if (result.ErrorCode == "COMPARE_QUANTITY_STALE")
                {
                    conflictLines.Add(line);
                }
                else
                {
                    _logger.LogWarning("Shopify error for {ItemNum}: [{Code}] {Message}",
                        line.PcaItemNum, result.ErrorCode, result.ErrorMessage);
                    errors.Add(new SyncItemError
                    {
                        PcaItemNum = line.PcaItemNum,
                        Category = SyncErrorCategory.NotFoundInShopify,
                        Detail = $"[{result.ErrorCode}] {result.ErrorMessage}",
                    });
                }
            }

            // Pass 2: unconditional retry for compare-and-set conflicts
            if (conflictLines.Count > 0)
            {
                _logger.LogWarning("{Count} compareQuantity conflicts — retrying unconditionally.", conflictLines.Count);

                Dictionary<string, InventorySetResult> retryResults;
                try
                {
                    retryResults = await _shopify.SetQuantitiesUnconditionalAsync(conflictLines, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unconditional retry Shopify API call failed.");
                    foreach (var line in conflictLines)
                        errors.Add(new SyncItemError
                        {
                            PcaItemNum = line.PcaItemNum,
                            Category = SyncErrorCategory.RetryFailed,
                            Detail = ex.Message,
                        });
                    continue;
                }

                foreach (var line in conflictLines)
                {
                    var retryResult = retryResults[line.PcaItemNum];
                    if (retryResult.IsSuccess)
                    {
                        var map = batchMapByItemNum[line.PcaItemNum];
                        successfulShopifyPushes.Add((map, line.Quantity));
                        pushedToShopify++;
                    }
                    else
                    {
                        _logger.LogError("Retry also failed for {ItemNum}: [{Code}] {Message}",
                            line.PcaItemNum, retryResult.ErrorCode, retryResult.ErrorMessage);
                        errors.Add(new SyncItemError
                        {
                            PcaItemNum = line.PcaItemNum,
                            Category = SyncErrorCategory.RetryFailed,
                            Detail = $"[{retryResult.ErrorCode}] {retryResult.ErrorMessage}",
                        });
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Step 6: Push Shopify→PCA
        // ------------------------------------------------------------------
        var successfulPcaPushes = new List<(ProductSyncMap Map, int NewQty)>();
        int pulledFromShopify = 0;

        foreach (var (map, newQty) in shopifyToPca)
        {
            try
            {
                var entity = await _pcaWriteDb.Inventory.FindAsync([map.PcaItemNum], ct);
                if (entity is null)
                {
                    _logger.LogWarning("PCA item {ItemNum} not found for Shopify→PCA write.", map.PcaItemNum);
                    errors.Add(new SyncItemError
                    {
                        PcaItemNum = map.PcaItemNum,
                        Category = SyncErrorCategory.PcaWriteFailed,
                        Detail = "Item not found in PCA Inventory table.",
                    });
                    continue;
                }

                entity.InStock = newQty;
                await _pcaWriteDb.SaveChangesAsync(ct);
                successfulPcaPushes.Add((map, newQty));
                pulledFromShopify++;
                _logger.LogInformation("Shopify→PCA: {ItemNum} qty set to {Qty}.", map.PcaItemNum, newQty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to write Shopify delta to PCA for {ItemNum}.", map.PcaItemNum);
                _pcaWriteDb.ChangeTracker.Clear();
                errors.Add(new SyncItemError
                {
                    PcaItemNum = map.PcaItemNum,
                    Category = SyncErrorCategory.PcaWriteFailed,
                    Detail = ex.Message,
                });
            }
        }

        // ------------------------------------------------------------------
        // Step 7: Update LastKnown* for all successfully synced items
        // ------------------------------------------------------------------

        // PCA→Shopify successes
        foreach (var (map, newQty) in successfulShopifyPushes)
        {
            var tracked = await _syncDb.ProductSyncMap.FindAsync([map.Id], ct);
            if (tracked is not null)
            {
                var pcaItem = pcaItems.FirstOrDefault(x =>
                    string.Equals(x.ItemNum, map.PcaItemNum, StringComparison.OrdinalIgnoreCase));
                var pcaQty = pcaItem is not null
                    ? (int)Math.Max(0, Math.Truncate(pcaItem.InStock))
                    : newQty;

                tracked.LastKnownQty = newQty;
                tracked.LastKnownPcaQty = pcaQty;
                tracked.LastKnownShopifyQty = newQty;
                tracked.LastSyncedAt = DateTime.UtcNow;
            }
        }

        // Shopify→PCA successes
        foreach (var (map, newQty) in successfulPcaPushes)
        {
            var tracked = await _syncDb.ProductSyncMap.FindAsync([map.Id], ct);
            if (tracked is not null)
            {
                shopifyQtyByItemId.TryGetValue(map.ShopifyInventoryItemId, out var shopifyQty);

                tracked.LastKnownQty = newQty;
                tracked.LastKnownPcaQty = newQty;
                tracked.LastKnownShopifyQty = shopifyQty;
                tracked.LastSyncedAt = DateTime.UtcNow;
            }
        }

        // Log conflict details
        int conflictsPcaWon = 0;
        foreach (var (_, map, newQty, shopifyDelta) in conflicts)
        {
            if (successfulShopifyPushes.Any(s => s.Map.Id == map.Id))
            {
                conflictsPcaWon++;
                errors.Add(new SyncItemError
                {
                    PcaItemNum = map.PcaItemNum,
                    Category = SyncErrorCategory.ConflictOverwritten,
                    Detail = $"Both sides changed. PCA won. Discarded Shopify delta={shopifyDelta}.",
                });
            }
        }

        if (successfulShopifyPushes.Count + successfulPcaPushes.Count > 0)
        {
            await _syncDb.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Updated LastKnown* for {ShopifyPushes} Shopify pushes and {PcaPushes} PCA pushes.",
                successfulShopifyPushes.Count, successfulPcaPushes.Count);
        }

        // ------------------------------------------------------------------
        // Step 8: Update SyncState.LastPolledAt
        // ------------------------------------------------------------------
        await UpdateSyncStateAsync(ct);

        return BuildResult(true, totalPca, changedItems, pushedToShopify, pulledFromShopify,
            conflictsPcaWon, notInSyncMap, errors);
    }

    private async Task UpdateSyncStateAsync(CancellationToken ct)
    {
        var state = await _syncDb.SyncState.FindAsync([1], ct);
        if (state is null)
        {
            _syncDb.SyncState.Add(new SyncState { Id = 1, LastPolledAt = DateTime.UtcNow });
        }
        else
        {
            state.LastPolledAt = DateTime.UtcNow;
        }
        await _syncDb.SaveChangesAsync(ct);
    }

    private static SyncResult BuildResult(
        bool success,
        int totalPca,
        int changedItems,
        int pushedToShopify,
        int pulledFromShopify,
        int conflictsPcaWon,
        int notInSyncMap,
        List<SyncItemError> errors) => new()
    {
        Success = success,
        CompletedAt = DateTime.UtcNow,
        TotalPcaItems = totalPca,
        ChangedItems = changedItems,
        PushedToShopify = pushedToShopify,
        PulledFromShopify = pulledFromShopify,
        ConflictsPcaWon = conflictsPcaWon,
        NotInSyncMapCount = notInSyncMap,
        Errors = errors,
    };
}
