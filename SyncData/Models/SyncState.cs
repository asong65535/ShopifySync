namespace SyncData.Models;

/// <summary>
/// Stores singleton sync runtime state (one row, Id = 1).
/// </summary>
public class SyncState
{
    public int Id { get; set; }

    /// <summary>Timestamp of the last completed poll cycle.</summary>
    public DateTime? LastPolledAt { get; set; }
}
