namespace SyncData.Models;

/// <summary>
/// Links a PCAmerica inventory item to its corresponding Shopify variant.
/// Populated by the one-time matching job (Phase 3) and updated by the sync service (Phase 4).
/// </summary>
public class ProductSyncMap
{
    public int Id { get; set; }

    /// <summary>PCAmerica Inventory.ItemNum — nvarchar, not int.</summary>
    public string PcaItemNum { get; set; } = string.Empty;

    /// <summary>UPC from Inventory_SKUS.AltSKU — fallback match key.</summary>
    public string? PcaUpc { get; set; }

    public long ShopifyProductId { get; set; }
    public long ShopifyVariantId { get; set; }
    public long ShopifyInventoryItemId { get; set; }
    public long ShopifyLocationId { get; set; }

    /// <summary>Last quantity pushed to Shopify. Used for delta detection.</summary>
    public decimal LastKnownQty { get; set; }

    public DateTime LastSyncedAt { get; set; }
}
