using System.Text.Json;
using PcaData.Models;
using SyncData;
using SyncData.Models;

namespace BootstrapJob.Bootstrap;

internal sealed class SyncMapWriter
{
    private readonly SyncDbContext _db;

    public SyncMapWriter(SyncDbContext db) => _db = db;

    /// <summary>
    /// Parses the bulk mutation result JSONL (productSet format) and writes ProductSyncMap rows.
    /// Each line: { "data": { "productSet": { "product": { "id": "...", "variants": {...} }, "userErrors": [...] } }, "__lineNumber": N }
    /// Returns count of rows written and count of lines skipped.
    /// </summary>
    public async Task<(int Written, int Skipped)> WriteAsync(
        Stream resultJsonl,
        IReadOnlyList<PcaInventoryItem> pcaItems,
        long locationId,
        CancellationToken ct = default)
    {
        var itemsByNum = pcaItems.ToDictionary(x => x.ItemNum, StringComparer.OrdinalIgnoreCase);

        var batch = new List<ProductSyncMap>();
        int written = 0;
        int skipped = 0;
        var now = DateTime.UtcNow;

        using var reader = new StreamReader(resultJsonl);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var lineNum = root.TryGetProperty("__lineNumber", out var ln) ? ln.GetInt64() : -1;

            // Check for userErrors first
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("productSet", out var ps))
            {
                var errors = ps.GetProperty("userErrors").EnumerateArray().ToList();
                if (errors.Count > 0)
                {
                    Console.WriteLine($"  WARN  Line {lineNum}: {errors[0].GetProperty("message").GetString()}");
                    skipped++;
                    continue;
                }

                if (!ps.TryGetProperty("product", out var product) ||
                    product.ValueKind == JsonValueKind.Null)
                {
                    Console.WriteLine($"  WARN  Line {lineNum}: productSet returned null product.");
                    skipped++;
                    continue;
                }

                var productGid = product.GetProperty("id").GetString()!;
                var productId = ParseGid(productGid);

                // Each product has one variant (we only create one per product)
                var variantEdges = product
                    .GetProperty("variants")
                    .GetProperty("edges")
                    .EnumerateArray();

                foreach (var edge in variantEdges)
                {
                    var node = edge.GetProperty("node");
                    var sku = node.GetProperty("sku").GetString() ?? "";
                    var variantId = ParseGid(node.GetProperty("id").GetString()!);
                    var inventoryItemId = ParseGid(
                        node.GetProperty("inventoryItem").GetProperty("id").GetString()!);

                    if (!itemsByNum.TryGetValue(sku, out var pcaItem))
                    {
                        Console.WriteLine($"  WARN  Variant SKU '{sku}' (line {lineNum}) not found in PCA items — skipped.");
                        skipped++;
                        continue;
                    }

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
                        written += batch.Count;
                        batch.Clear();
                    }
                }
            }
        }

        if (batch.Count > 0)
        {
            _db.ProductSyncMap.AddRange(batch);
            await _db.SaveChangesAsync(ct);
            written += batch.Count;
        }

        return (written, skipped);
    }

    private static long ParseGid(string gid)
    {
        var segment = gid.Split('/').Last();
        if (!long.TryParse(segment, out var id))
            throw new FormatException($"Cannot parse Shopify GID: '{gid}'");
        return id;
    }
}
