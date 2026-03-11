namespace SyncJob.Shopify;

/// <summary>One line item in an inventorySetQuantities batch.</summary>
internal sealed class InventoryQuantityLine
{
    public required string InventoryItemGid { get; init; }
    public required string LocationGid { get; init; }
    public required int Quantity { get; init; }

    /// <summary>Null on unconditional (retry) pass.</summary>
    public int? CompareQuantity { get; init; }

    /// <summary>Original PCA item number — carried for error reporting only.</summary>
    public required string PcaItemNum { get; init; }
}
