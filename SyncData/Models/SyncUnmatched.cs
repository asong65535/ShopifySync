namespace SyncData.Models;

/// <summary>
/// Records PCAmerica items that could not be matched to a Shopify variant.
/// Populated by the one-time matching job for manual review.
/// </summary>
public class SyncUnmatched
{
    public int Id { get; set; }

    public string PcaItemNum { get; set; } = string.Empty;
    public string? PcaItemName { get; set; }
    public string? PcaUpc { get; set; }

    /// <summary>Human-readable reason the item could not be matched.</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime LoggedAt { get; set; }
}
