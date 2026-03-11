namespace SyncHistory;

public sealed class SyncHistoryItemError
{
    public required string PcaItemNum { get; init; }
    public required string Category { get; init; }
    public string? Detail { get; init; }
}
