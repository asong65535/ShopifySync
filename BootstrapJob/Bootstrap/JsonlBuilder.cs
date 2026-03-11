using System.Text;
using System.Text.Json;
using PcaData.Models;

namespace BootstrapJob.Bootstrap;

internal sealed class JsonlBuilder
{
    // No-BOM UTF-8 — Shopify's bulk mutation parser chokes on the BOM
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Builds a JSONL stream where each line is a productSet input object.
    /// Returns the stream (position 0), item count written, and count of clamped negative-stock items.
    /// </summary>
    public (MemoryStream Stream, int Written, int NegativeClamped) Build(
        IReadOnlyList<PcaInventoryItem> items,
        long locationId)
    {
        var locationGid = $"gid://shopify/Location/{locationId}";
        var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Utf8NoBom, leaveOpen: true);

        int written = 0;
        int negativeClamped = 0;

        foreach (var item in items)
        {
            var qty = (int)Math.Max(0, Math.Truncate(item.InStock));
            if (qty == 0 && item.InStock < 0)
                negativeClamped++;

            // Pick first non-empty UPC; null if none
            var upc = item.Skus
                .Select(s => s.AltSku)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

            if (item.Skus.Count(s => !string.IsNullOrWhiteSpace(s.AltSku)) > 1)
                Console.WriteLine($"  WARN  [{item.ItemNum}] has multiple UPCs — using first: {upc}");

            var line = BuildLine(item.ItemName, item.ItemNum, upc, qty, locationGid);
            writer.WriteLine(line);
            written++;
        }

        writer.Flush();
        ms.Position = 0;
        return (ms, written, negativeClamped);
    }

    private static string BuildLine(
        string title,
        string sku,
        string? barcode,
        int qty,
        string locationGid)
    {
        using var ms = new MemoryStream();
        using var jw = new Utf8JsonWriter(ms);

        // productSet input format (2025-10):
        // { "input": { "title": "...", "status": "DRAFT", "variants": [{ ... }] } }
        jw.WriteStartObject();
        jw.WriteStartObject("input");

        jw.WriteString("title", title);
        jw.WriteString("status", "DRAFT");

        // productOptions must be declared alongside variants for productSet
        jw.WriteStartArray("productOptions");
        jw.WriteStartObject();
        jw.WriteString("name", "Title");
        jw.WriteStartArray("values");
        jw.WriteStartObject();
        jw.WriteString("name", "Default Title");
        jw.WriteEndObject();
        jw.WriteEndArray();
        jw.WriteEndObject();
        jw.WriteEndArray();

        jw.WriteStartArray("variants");
        jw.WriteStartObject();
        jw.WriteString("sku", sku);
        if (barcode is not null)
            jw.WriteString("barcode", barcode);

        // optionValues is required (NON_NULL) on ProductVariantSetInput
        jw.WriteStartArray("optionValues");
        jw.WriteStartObject();
        jw.WriteString("optionName", "Title");
        jw.WriteString("name", "Default Title");
        jw.WriteEndObject();
        jw.WriteEndArray();

        // ProductSetInventoryInput: { locationId, name, quantity }
        // "name" is the inventory state — "available" for on-hand stock
        jw.WriteStartArray("inventoryQuantities");
        jw.WriteStartObject();
        jw.WriteString("locationId", locationGid);
        jw.WriteString("name", "available");
        jw.WriteNumber("quantity", qty);
        jw.WriteEndObject();
        jw.WriteEndArray();

        jw.WriteEndObject();
        jw.WriteEndArray(); // variants

        jw.WriteEndObject(); // input
        jw.WriteEndObject();

        jw.Flush();
        return Utf8NoBom.GetString(ms.ToArray());
    }
}
