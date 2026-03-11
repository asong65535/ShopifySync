using System.ComponentModel.DataAnnotations.Schema;

namespace PcaData.Models;

/// <summary>
/// Read-only model for the PCAmerica <c>Inventory_SKUS</c> table.
/// Holds barcode/UPC entries per item. One item may have multiple rows here.
/// Natural key is (ItemNum, AltSKU) — there is no surrogate PK column.
/// </summary>
[Table("Inventory_SKUS")]
public class PcaInventorySku
{
    /// <summary>Foreign key back to <see cref="PcaInventoryItem.ItemNum"/>.</summary>
    [Column("ItemNum")]
    public string ItemNum { get; set; } = string.Empty;

    /// <summary>The UPC / barcode value. Use this for Shopify matching.</summary>
    [Column("AltSKU")]
    public string? AltSku { get; set; }

    // Navigation property
    [ForeignKey(nameof(ItemNum))]
    public PcaInventoryItem Item { get; set; } = null!;
}
