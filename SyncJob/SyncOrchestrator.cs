using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PcaData;
using SyncData;
using SyncData.Models;
using SyncJob.Shopify;

namespace SyncJob;

/// <summary>
/// Executes one delta sync run. Injected into SyncService.
/// </summary>
public sealed class SyncOrchestrator
{
    private const int BatchSize = 250;

    private readonly PcAmericaDbContext _pcaDb;
    private readonly SyncDbContext _syncDb;
    private readonly ShopifyClient _shopify;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        PcAmericaDbContext pcaDb,
        SyncDbContext syncDb,
        ShopifyClient shopify,
        ILogger<SyncOrchestrator> logger)
    {
        _pcaDb = pcaDb;
        _syncDb = syncDb;
        _shopify = shopify;
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
        // Step 4: Filter to changed items only
        // ------------------------------------------------------------------
        var changed = pcaItems
            .Where(x => mapByItemNum.TryGetValue(x.ItemNum, out var m)
                && (int)Math.Max(0, Math.Truncate(x.InStock)) != (int)Math.Max(0, Math.Truncate(m.LastKnownQty)))
            .Select(x => (Item: x, Map: mapByItemNum[x.ItemNum]))
            .ToList();

        changedItems = changed.Count;
        _logger.LogInformation("{Count} items have changed quantity.", changedItems);

        if (changedItems == 0)
        {
            await UpdateSyncStateAsync(ct);
            return BuildResult(true, totalPca, changedItems, pushedToShopify, notInSyncMap, errors);
        }

        // ------------------------------------------------------------------
        // Step 5: Process in batches
        // ------------------------------------------------------------------
        var successfulMaps = new List<(ProductSyncMap Map, int NewQty)>();

        for (int i = 0; i < changed.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = changed.Skip(i).Take(BatchSize).ToList();

            var lines = batch.Select(pair =>
            {
                var qty = (int)Math.Max(0, Math.Truncate(pair.Item.InStock));
                var compareQty = (int)Math.Truncate(pair.Map.LastKnownQty);
                return new InventoryQuantityLine
                {
                    InventoryItemGid = ShopifyClient.ToGid("InventoryItem", pair.Map.ShopifyInventoryItemId),
                    LocationGid = ShopifyClient.ToGid("Location", pair.Map.ShopifyLocationId),
                    Quantity = qty,
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
                    successfulMaps.Add((map, line.Quantity));
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

            // Pass 2: unconditional retry for conflicts
            if (conflictLines.Count > 0)
            {
                _logger.LogWarning("{Count} compareQuantity conflicts — retrying unconditionally (BNM wins).", conflictLines.Count);

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
                        successfulMaps.Add((map, line.Quantity));
                        pushedToShopify++;
                        errors.Add(new SyncItemError
                        {
                            PcaItemNum = line.PcaItemNum,
                            Category = SyncErrorCategory.ConflictOverwritten,
                            Detail = "compareQuantity was stale; overwrote Shopify quantity with PCA value.",
                        });
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
        // Step 8: Update LastKnownQty for all successfully pushed items
        // ------------------------------------------------------------------
        if (successfulMaps.Count > 0)
        {
            foreach (var (map, newQty) in successfulMaps)
            {
                var tracked = await _syncDb.ProductSyncMap.FindAsync([map.Id], ct);
                if (tracked is not null)
                {
                    tracked.LastKnownQty = newQty;
                    tracked.LastSyncedAt = DateTime.UtcNow;
                }
            }

            await _syncDb.SaveChangesAsync(ct);
            _logger.LogInformation("Updated LastKnownQty for {Count} items.", successfulMaps.Count);
        }

        // ------------------------------------------------------------------
        // Step 9: Update SyncState.LastPolledAt
        // ------------------------------------------------------------------
        await UpdateSyncStateAsync(ct);

        return BuildResult(true, totalPca, changedItems, pushedToShopify, notInSyncMap, errors);
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
        int notInSyncMap,
        List<SyncItemError> errors) => new()
    {
        Success = success,
        CompletedAt = DateTime.UtcNow,
        TotalPcaItems = totalPca,
        ChangedItems = changedItems,
        PushedToShopify = pushedToShopify,
        NotInSyncMapCount = notInSyncMap,
        Errors = errors,
    };
}
