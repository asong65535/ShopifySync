namespace SyncJob;

/// <summary>
/// A single per-item error recorded during a sync run.
/// </summary>
public sealed class SyncItemError
{
    public required string PcaItemNum { get; init; }
    public required SyncErrorCategory Category { get; init; }

    /// <summary>Raw Shopify userError message or exception message for diagnostics.</summary>
    public string? Detail { get; init; }
}
