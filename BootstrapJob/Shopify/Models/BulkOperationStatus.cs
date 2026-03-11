namespace BootstrapJob.Shopify.Models;

internal sealed record BulkOperationStatus(
    string Id,
    string Status,
    string? ErrorCode,
    string? Url,
    long ObjectCount);
