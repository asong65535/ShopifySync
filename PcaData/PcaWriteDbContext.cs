using Microsoft.EntityFrameworkCore;
using PcaData.Models;

namespace PcaData;

/// <summary>
/// Narrow write context for PCA Inventory.In_Stock updates.
/// Separate from the read-only PcAmericaDbContext to preserve its safety guard.
/// Uses the raw PcAmerica connection string (no ApplicationIntent=ReadOnly).
/// </summary>
public class PcaWriteDbContext : DbContext
{
    public PcaWriteDbContext(DbContextOptions<PcaWriteDbContext> options)
        : base(options) { }

    public DbSet<PcaInventoryWrite> Inventory { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PcaInventoryWrite>(e =>
        {
            e.ToTable("Inventory");
            e.HasKey(x => x.ItemNum);
            e.Property(x => x.ItemNum).HasColumnName("ItemNum").HasMaxLength(50);
            e.Property(x => x.InStock).HasColumnName("In_Stock").HasColumnType("decimal(18,4)");
        });
    }
}
