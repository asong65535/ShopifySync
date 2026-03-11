using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BootstrapJob.Shopify.Models;
using Microsoft.Extensions.Configuration;

namespace BootstrapJob.Shopify;

internal sealed class ShopifyClient
{
    private readonly HttpClient _http;

    public ShopifyClient(HttpClient http, IConfiguration config)
    {
        var storeUrl = config["Shopify:StoreUrl"]
            ?? throw new InvalidOperationException("Shopify:StoreUrl is missing from configuration.");
        var token = config["Shopify:AccessToken"]
            ?? throw new InvalidOperationException("Shopify:AccessToken is missing from configuration.");

        // Accept either "store.myshopify.com" or "https://store.myshopify.com"
        var host = storeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(storeUrl).Host
            : storeUrl;
        http.BaseAddress = new Uri($"https://{host}/admin/api/2025-10/");
        http.DefaultRequestHeaders.Add("X-Shopify-Access-Token", token);
        _http = http;
    }

    /// <summary>Posts a GraphQL query/mutation. Returns the parsed JsonDocument on success.</summary>
    public async Task<JsonDocument> PostGraphqlAsync(
        string query,
        object? variables = null,
        CancellationToken ct = default)
    {
        var payload = variables is null
            ? new { query }
            : (object)new { query, variables };

        using var request = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = null!;

        // Simple 3-attempt retry for transient errors (429, 5xx)
        for (int attempt = 1; attempt <= 3; attempt++)
        {
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

        var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        return doc;
    }

    /// <summary>PUT upload to the presigned staged upload URL.</summary>
    public async Task UploadFileAsync(
        string uploadUrl,
        Stream content,
        CancellationToken ct = default)
    {
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var response = await _http.PutAsync(uploadUrl, streamContent, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new ShopifyApiException(
                $"Staged upload PUT failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}",
                (int)response.StatusCode);
        }
    }

    /// <summary>Downloads the bulk operation result JSONL file.</summary>
    public async Task<Stream> DownloadResultAsync(string url, CancellationToken ct = default)
    {
        // Result URL is external (e.g. storage.googleapis.com) — use a plain HttpClient
        using var plainHttp = new HttpClient();
        var response = await plainHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new ShopifyApiException(
                $"Result download failed: {(int)response.StatusCode}",
                (int)response.StatusCode);

        // Copy to MemoryStream so the HttpClient can be disposed
        var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    // -------------------------------------------------------------------------
    // Typed helpers
    // -------------------------------------------------------------------------

    public async Task<List<ShopifyLocation>> FetchLocationsAsync(CancellationToken ct = default)
    {
        using var doc = await PostGraphqlAsync(ShopifyGraphql.ListLocations, ct: ct);
        var locations = new List<ShopifyLocation>();

        foreach (var edge in doc.RootElement
            .GetProperty("data")
            .GetProperty("locations")
            .GetProperty("edges")
            .EnumerateArray())
        {
            var node = edge.GetProperty("node");
            var gid = node.GetProperty("id").GetString()!;
            locations.Add(new ShopifyLocation(ParseGid(gid), gid));
        }

        return locations;
    }

    public async Task<StagedUploadTarget> CreateStagedUploadAsync(CancellationToken ct = default)
    {
        var variables = new
        {
            input = new[]
            {
                new
                {
                    resource = "BULK_MUTATION_VARIABLES",
                    filename = "products.jsonl",
                    mimeType = "text/jsonl",
                    httpMethod = "PUT"
                }
            }
        };

        using var doc = await PostGraphqlAsync(ShopifyGraphql.StagedUploadsCreate, variables, ct);
        var root = doc.RootElement.GetProperty("data").GetProperty("stagedUploadsCreate");

        var errors = root.GetProperty("userErrors").EnumerateArray().ToList();
        if (errors.Count > 0)
            throw new ShopifyApiException(
                $"stagedUploadsCreate error: {errors[0].GetProperty("message").GetString()}");

        var target = root.GetProperty("stagedTargets")[0];
        var url = target.GetProperty("url").GetString()!;
        var resourceUrl = target.GetProperty("resourceUrl").GetString()!;

        var parameters = target.GetProperty("parameters")
            .EnumerateArray()
            .Select(p => new StagedUploadParameter(
                p.GetProperty("name").GetString()!,
                p.GetProperty("value").GetString()!))
            .ToList();

        return new StagedUploadTarget(url, resourceUrl, parameters);
    }

    public async Task<string> RunBulkOperationAsync(
        string stagedUploadPath,
        CancellationToken ct = default)
    {
        var variables = new
        {
            mutation = ShopifyGraphql.ProductSetTemplate,
            stagedUploadPath
        };

        using var doc = await PostGraphqlAsync(ShopifyGraphql.BulkOperationRunMutation, variables, ct);
        var root = doc.RootElement.GetProperty("data").GetProperty("bulkOperationRunMutation");

        var errors = root.GetProperty("userErrors").EnumerateArray().ToList();
        if (errors.Count > 0)
            throw new ShopifyApiException(
                $"bulkOperationRunMutation error: {errors[0].GetProperty("message").GetString()}");

        return root.GetProperty("bulkOperation").GetProperty("id").GetString()!;
    }

    public async Task<BulkOperationStatus?> PollBulkOperationAsync(string bulkOpId, CancellationToken ct = default)
    {
        using var doc = await PostGraphqlAsync(
            ShopifyGraphql.PollBulkOperationById,
            new { id = bulkOpId },
            ct);
        var op = doc.RootElement.GetProperty("data").GetProperty("node");

        // null means no active bulk operation visible yet — caller should retry
        if (op.ValueKind == JsonValueKind.Null)
            return null;

        // objectCount is returned as a string by Shopify ("123"), not a number
        long objectCount = 0;
        if (op.TryGetProperty("objectCount", out var oc))
        {
            if (oc.ValueKind == JsonValueKind.String)
                long.TryParse(oc.GetString(), out objectCount);
            else if (oc.ValueKind == JsonValueKind.Number)
                objectCount = oc.GetInt64();
        }

        return new BulkOperationStatus(
            Id:          op.GetProperty("id").GetString()!,
            Status:      op.GetProperty("status").GetString()!,
            ErrorCode:   op.TryGetProperty("errorCode", out var ec) ? ec.GetString() : null,
            Url:         op.TryGetProperty("url", out var u) ? u.GetString() : null,
            ObjectCount: objectCount);
    }

    public static long ParseGid(string gid)
    {
        var segment = gid.Split('/').Last();
        if (!long.TryParse(segment, out var id))
            throw new FormatException($"Cannot parse Shopify GID: '{gid}'");
        return id;
    }
}
