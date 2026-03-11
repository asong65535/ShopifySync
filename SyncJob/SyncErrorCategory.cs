namespace SyncJob;

/// <summary>
/// Named categories for per-item sync failures. Mapped to friendly messages by the UI layer.
/// </summary>
public enum SyncErrorCategory
{
    /// <summary>No ProductSyncMap row exists for this PCA item.</summary>
    NotInSyncMap,

    /// <summary>Shopify returned INVALID_INVENTORY_ITEM, INVALID_LOCATION, ITEM_NOT_STOCKED_AT_LOCATION, or NON_MUTABLE_INVENTORY_ITEM.</summary>
    NotFoundInShopify,

    /// <summary>compareQuantity mismatch — retried unconditionally and overwrote Shopify qty.</summary>
    ConflictOverwritten,

    /// <summary>Unconditional retry also failed with a Shopify error.</summary>
    RetryFailed,
}
