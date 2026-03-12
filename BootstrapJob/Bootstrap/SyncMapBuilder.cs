using System.Text.Json;
using BootstrapJob.Shopify;
using PcaData.Models;
using SyncData;
using SyncData.Models;

namespace BootstrapJob.Bootstrap;

internal sealed class SyncMapBuilder
{
    private readonly SyncDbContext _db;

    public SyncMapBuilder(SyncDbContext db) => _db = db;

    /// <summary>
    /// Parses a bulk query result JSONL (product + variant parent/child lines)
    /// and writes ProductSyncMap rows by matching variant SKU to PCA ItemNum.
    ///
    /// Bulk query JSONL format:
    ///   {"id":"gid://shopify/Product/123"}
    ///   {"id":"gid://shopify/ProductVariant/456","sku":"ITEM001","inventoryItem":{"id":"gid://shopify/InventoryItem/789"},"__parentId":"gid://shopify/Product/123"}
    /// </summary>
    public async Task<(int Matched, int UnmatchedShopify)> WriteAsync(
        Stream resultJsonl,
        IReadOnlyList<PcaInventoryItem> pcaItems,
        long locationId,
        CancellationToken ct = default)
    {
        var itemsByNum = pcaItems.ToDictionary(x => x.ItemNum, StringComparer.OrdinalIgnoreCase);

        var batch = new List<ProductSyncMap>();
        int matched = 0;
        int unmatchedShopify = 0;
        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        using var reader = new StreamReader(resultJsonl);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Variant lines have __parentId; product lines do not
            if (!root.TryGetProperty("__parentId", out var parentIdProp))
                continue;

            var parentGid = parentIdProp.GetString()!;
            var productId = ShopifyClient.ParseGid(parentGid);

            var variantGid = root.GetProperty("id").GetString()!;
            var variantId = ShopifyClient.ParseGid(variantGid);

            var sku = root.TryGetProperty("sku", out var skuProp)
                ? skuProp.GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(sku))
            {
                unmatchedShopify++;
                continue;
            }

            var inventoryItemGid = root.GetProperty("inventoryItem").GetProperty("id").GetString()!;
            var inventoryItemId = ShopifyClient.ParseGid(inventoryItemGid);

            if (!itemsByNum.TryGetValue(sku, out var pcaItem))
            {
                Console.WriteLine($"  WARN  Shopify SKU '{sku}' (variant {variantId}) not found in PCA — skipped.");
                unmatchedShopify++;
                continue;
            }

            matchedKeys.Add(sku);

            var upc = pcaItem.Skus
                .Select(s => s.AltSku)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

            batch.Add(new ProductSyncMap
            {
                PcaItemNum             = pcaItem.ItemNum,
                PcaUpc                 = upc,
                ShopifyProductId       = productId,
                ShopifyVariantId       = variantId,
                ShopifyInventoryItemId = inventoryItemId,
                ShopifyLocationId      = locationId,
                LastKnownQty           = (int)Math.Max(0, Math.Truncate(pcaItem.InStock)),
                LastSyncedAt           = now
            });

            if (batch.Count >= 100)
            {
                _db.ProductSyncMap.AddRange(batch);
                await _db.SaveChangesAsync(ct);
                matched += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            _db.ProductSyncMap.AddRange(batch);
            await _db.SaveChangesAsync(ct);
            matched += batch.Count;
        }

        // Report PCA items with no Shopify match
        var unmatchedPca = itemsByNum.Keys
            .Where(k => !matchedKeys.Contains(k))
            .ToList();

        if (unmatchedPca.Count > 0)
        {
            Console.WriteLine($"\n  {unmatchedPca.Count} PCA items had no Shopify match:");
            foreach (var key in unmatchedPca.Take(10))
                Console.WriteLine($"    {key}");
            if (unmatchedPca.Count > 10)
                Console.WriteLine($"    ... and {unmatchedPca.Count - 10} more.");
        }

        return (matched, unmatchedShopify);
    }

    // ParseGid delegated to ShopifyClient.ParseGid (already public static)
}
