using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SyncJob.Shopify;

/// <summary>
/// Typed Shopify GraphQL client for the sync job.
/// Registered as a singleton by AddSyncJob().
/// </summary>
public sealed class ShopifyClient
{
    private readonly HttpClient _http;

    public ShopifyClient(HttpClient http, IConfiguration config)
    {
        var storeUrl = config["Shopify:StoreUrl"]
            ?? throw new InvalidOperationException("Shopify:StoreUrl is missing from configuration.");
        var token = config["Shopify:AccessToken"]
            ?? throw new InvalidOperationException("Shopify:AccessToken is missing from configuration.");

        var host = storeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(storeUrl).Host
            : storeUrl;

        http.BaseAddress = new Uri($"https://{host}/admin/api/2025-10/");
        http.DefaultRequestHeaders.Add("X-Shopify-Access-Token", token);
        // Shopify inventorySetQuantities can be slow under load — allow up to 3 minutes per request.
        // The retry loop handles 429/5xx on top of this; each attempt gets its own timeout window.
        http.Timeout = TimeSpan.FromMinutes(3);
        _http = http;
    }

    /// <summary>
    /// Test constructor — accepts a pre-configured HttpClient (e.g. with a fake handler).
    /// Bypasses config parsing entirely; base address and auth headers are already set by the caller.
    /// </summary>
    internal ShopifyClient(HttpClient http)
    {
        _http = http;
    }

    // -------------------------------------------------------------------------
    // inventorySetQuantities — compare-and-set pass
    // -------------------------------------------------------------------------

    /// <summary>
    /// Calls inventorySetQuantities with compareQuantity on each line item.
    /// Returns a dictionary keyed by PcaItemNum → result (success or error code).
    /// Lines with COMPARE_QUANTITY_STALE are surfaced so the caller can retry unconditionally.
    /// </summary>
    internal async Task<Dictionary<string, InventorySetResult>> SetQuantitiesWithCompareAsync(
        IReadOnlyList<InventoryQuantityLine> lines,
        CancellationToken ct = default)
    {
        var quantities = lines.Select(l => new
        {
            inventoryItemId = l.InventoryItemGid,
            locationId = l.LocationGid,
            quantity = l.Quantity,
            compareQuantity = l.CompareQuantity,
        }).ToArray();

        var input = new
        {
            name = "available",
            reason = "correction",
            ignoreCompareQuantity = false,
            quantities,
        };

        return await PostAndParseAsync(lines, input, ct);
    }

    // -------------------------------------------------------------------------
    // inventorySetQuantities — unconditional (retry) pass
    // -------------------------------------------------------------------------

    /// <summary>
    /// Calls inventorySetQuantities with ignoreCompareQuantity: true.
    /// Used as the conflict-resolution retry path — BNM always wins.
    /// </summary>
    internal async Task<Dictionary<string, InventorySetResult>> SetQuantitiesUnconditionalAsync(
        IReadOnlyList<InventoryQuantityLine> lines,
        CancellationToken ct = default)
    {
        var quantities = lines.Select(l => new
        {
            inventoryItemId = l.InventoryItemGid,
            locationId = l.LocationGid,
            quantity = l.Quantity,
        }).ToArray();

        var input = new
        {
            name = "available",
            reason = "correction",
            ignoreCompareQuantity = true,
            quantities,
        };

        return await PostAndParseAsync(lines, input, ct);
    }

    // -------------------------------------------------------------------------
    // Shared internals
    // -------------------------------------------------------------------------

    private async Task<Dictionary<string, InventorySetResult>> PostAndParseAsync(
        IReadOnlyList<InventoryQuantityLine> lines,
        object input,
        CancellationToken ct)
    {
        using var doc = await PostGraphqlAsync(
            ShopifyGraphql.InventorySetQuantitiesWithCompare,
            new { input },
            ct);

        var results = new Dictionary<string, InventorySetResult>(lines.Count);

        // Pre-populate with success — userErrors only lists failures
        foreach (var line in lines)
            results[line.PcaItemNum] = new InventorySetResult();

        var root = doc.RootElement
            .GetProperty("data")
            .GetProperty("inventorySetQuantities");

        foreach (var err in root.GetProperty("userErrors").EnumerateArray())
        {
            var code = err.GetProperty("code").GetString()!;
            var message = err.GetProperty("message").GetString();

            // field path: ["quantities", "0", "inventoryItemId"] — extract index to map back to line
            var fieldArr = err.GetProperty("field").EnumerateArray().ToList();
            if (fieldArr.Count >= 2
                && fieldArr[0].GetString() == "quantities"
                && int.TryParse(fieldArr[1].GetString(), out var idx)
                && idx < lines.Count)
            {
                var itemNum = lines[idx].PcaItemNum;
                results[itemNum] = new InventorySetResult { ErrorCode = code, ErrorMessage = message };
            }
            else
            {
                // Unexpected field path shape — log and mark entire batch failed
                var rawField = string.Join(", ", fieldArr.Select(f => f.GetString()));
                // Note: we don't have an ILogger here; the caller logs the per-item error codes.
                // Batch-level fallback: mark all lines as failed
                foreach (var line in lines)
                    results[line.PcaItemNum] = new InventorySetResult { ErrorCode = code, ErrorMessage = $"[field={rawField}] {message}" };
                break;
            }
        }

        return results;
    }

    internal async Task<JsonDocument> PostGraphqlAsync(
        string query,
        object? variables = null,
        CancellationToken ct = default)
    {
        var payload = variables is null
            ? new { query }
            : (object)new { query, variables };

        // Serialize once; re-create StringContent per attempt because the stream is consumed on send.
        var serialized = JsonSerializer.Serialize(payload);

        HttpResponseMessage response = null!;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var request = new StringContent(serialized, Encoding.UTF8, "application/json");
            response = await _http.PostAsync("graphql.json", request, ct);

            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                if (attempt < 3)
                {
                    await Task.Delay(5_000 * attempt, ct);
                    continue;
                }
            }
            break;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new ShopifyApiException(
                $"Shopify GraphQL request failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}",
                (int)response.StatusCode);
        }

        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    public static string ToGid(string type, long id) => $"gid://shopify/{type}/{id}";
}
