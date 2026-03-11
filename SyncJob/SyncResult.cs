namespace SyncJob;

/// <summary>
/// The outcome of a single sync run. Returned by SyncService.RunAsync and fired in SyncCompleted.
/// </summary>
public sealed class SyncResult
{
    public bool Success { get; init; }

    /// <summary>UTC timestamp when this run completed.</summary>
    public DateTime CompletedAt { get; init; }

    public int TotalPcaItems { get; init; }
    public int ChangedItems { get; init; }
    public int PushedToShopify { get; init; }

    /// <summary>Items with no ProductSyncMap row — expected when PCA has new items not yet in Shopify.</summary>
    public int NotInSyncMapCount { get; init; }

    /// <summary>Per-item errors, grouped by category for UI display.</summary>
    public IReadOnlyList<SyncItemError> Errors { get; init; } = [];

    /// <summary>Top-level failure message when the run itself could not complete (e.g. DB unreachable).</summary>
    public string? FatalError { get; init; }

    public static SyncResult Fatal(string message) => new()
    {
        Success = false,
        CompletedAt = DateTime.UtcNow,
        FatalError = message,
    };
}
