using SyncHistory;
using SyncJob;

namespace ShopifySyncApp.Services;

public static class SyncResultHistoryMapper
{
    public static Task WriteAsync(HistoryWriter writer, SyncResult result) =>
        writer.WriteAsync(
            result.Success,
            result.CompletedAt,
            result.TotalPcaItems,
            result.ChangedItems,
            result.PushedToShopify,
            result.PulledFromShopify,
            result.ConflictsPcaWon,
            result.NotInSyncMapCount,
            result.Errors.Select(e => (e.PcaItemNum, e.Category.ToString(), e.Detail)),
            result.FatalError);
}
