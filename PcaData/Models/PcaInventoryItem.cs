using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PcaData.Models;

/// <summary>
/// Read-only model for the PCAmerica <c>Inventory</c> table.
/// This table is both the master product catalog and the stock quantity store.
/// </summary>
[Table("Inventory")]
public class PcaInventoryItem
{
    /// <summary>Primary key. nvarchar — do NOT treat as int.</summary>
    [Key]
    [Column("ItemNum")]
    public string ItemNum { get; set; } = string.Empty;

    [Column("ItemName")]
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Quantity on hand. Stored as decimal in PCAmerica.</summary>
    [Column("In_Stock")]
    public decimal InStock { get; set; }

    /// <summary>Soft-delete flag. Always filter with IsDeleted == false.</summary>
    [Column("IsDeleted")]
    public bool IsDeleted { get; set; }

    // Navigation property — one item can have multiple UPC/barcode entries
    public ICollection<PcaInventorySku> Skus { get; set; } = new List<PcaInventorySku>();
}
