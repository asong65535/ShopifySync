using System.Text.Json;

namespace SyncHistory;

public sealed class HistoryWriter
{
    private readonly string? _directoryOverride;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public HistoryWriter(string? directoryOverride = null)
    {
        _directoryOverride = directoryOverride;
    }

    public async Task WriteAsync(
        bool success,
        DateTime completedAt,
        int totalPcaItems,
        int changedItems,
        int pushedToShopify,
        int pulledFromShopify,
        int conflictsPcaWon,
        int notInSyncMapCount,
        IEnumerable<(string PcaItemNum, string Category, string? Detail)> errors,
        string? fatalError)
    {
        var record = new SyncHistoryRecord
        {
            Success = success,
            CompletedAt = completedAt,
            TotalPcaItems = totalPcaItems,
            ChangedItems = changedItems,
            PushedToShopify = pushedToShopify,
            PulledFromShopify = pulledFromShopify,
            ConflictsPcaWon = conflictsPcaWon,
            NotInSyncMapCount = notInSyncMapCount,
            Errors = errors.Select(e => new SyncHistoryItemError
            {
                PcaItemNum = e.PcaItemNum,
                Category = e.Category,
                Detail = e.Detail,
            }).ToList(),
            FatalError = fatalError,
        };

        var dir = _directoryOverride ?? HistoryPaths.LogsDirectory();
        var path = Path.Combine(dir, HistoryPaths.FileNameFor(completedAt));
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, record, Options);
    }
}
