namespace PcaData.Models;

/// <summary>
/// Narrow write model for updating PCA Inventory.In_Stock.
/// Used only by PcaWriteDbContext for Shopify→PCA sync.
/// </summary>
public class PcaInventoryWrite
{
    public string ItemNum { get; set; } = null!;
    public decimal InStock { get; set; }
}
