namespace SyncHistory;

public sealed class SyncHistoryRecord
{
    public bool Success { get; init; }
    public DateTime CompletedAt { get; init; }
    public int TotalPcaItems { get; init; }
    public int ChangedItems { get; init; }
    public int PushedToShopify { get; init; }
    public int PulledFromShopify { get; init; }
    public int ConflictsPcaWon { get; init; }
    public int NotInSyncMapCount { get; init; }
    public List<SyncHistoryItemError> Errors { get; init; } = [];
    public string? FatalError { get; init; }
}
