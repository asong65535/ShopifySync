namespace SyncJob.Shopify;

/// <summary>Per-item outcome from a single inventorySetQuantities call.</summary>
public sealed class InventorySetResult
{
    /// <summary>Error code from Shopify userErrors, or null on success.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Raw error message, or null on success.</summary>
    public string? ErrorMessage { get; init; }

    public bool IsSuccess => ErrorCode is null;
}
