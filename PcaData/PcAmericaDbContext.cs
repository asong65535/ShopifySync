using Microsoft.EntityFrameworkCore;
using PcaData.Models;

namespace PcaData;

/// <summary>
/// Read-only EF Core context for the PCAmerica (CRE/RPE) database.
/// 
/// Rules:
///   - Never call SaveChanges / SaveChangesAsync — both throw by design.
///   - All queries use NoTracking — no change tracker overhead.
///   - Always exclude soft-deleted items: .Where(x => !x.IsDeleted)
/// </summary>
public class PcAmericaDbContext : DbContext
{
    public PcAmericaDbContext(DbContextOptions<PcAmericaDbContext> options)
        : base(options) { }

    public DbSet<PcaInventoryItem> Inventory { get; set; } = null!;
    public DbSet<PcaInventorySku> InventorySkus { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PcaInventoryItem>(e =>
        {
            e.ToTable("Inventory");
            e.HasKey(x => x.ItemNum);
            e.Property(x => x.ItemNum).HasColumnName("ItemNum").HasMaxLength(50);
            e.Property(x => x.ItemName).HasColumnName("ItemName").HasMaxLength(255);
            e.Property(x => x.InStock).HasColumnName("In_Stock").HasColumnType("decimal(18,4)");
            e.Property(x => x.IsDeleted).HasColumnName("IsDeleted");

            // Relationship: one Inventory row → many Inventory_SKUS rows
            e.HasMany(x => x.Skus)
             .WithOne(s => s.Item)
             .HasForeignKey(s => s.ItemNum)
             .HasPrincipalKey(x => x.ItemNum);
        });

        mb.Entity<PcaInventorySku>(e =>
        {
            e.ToTable("Inventory_SKUS");
            e.HasKey(x => new { x.ItemNum, x.AltSku });
            e.Property(x => x.ItemNum).HasColumnName("ItemNum").HasMaxLength(50);
            e.Property(x => x.AltSku).HasColumnName("AltSKU").HasMaxLength(100);
        });
    }

    // -------------------------------------------------------------------------
    // Safety guards — this DB is read-only; writes must never reach PCAmerica
    // -------------------------------------------------------------------------

    public override int SaveChanges() =>
        throw new InvalidOperationException("PcAmericaDbContext is read-only. Do not call SaveChanges.");

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("PcAmericaDbContext is read-only. Do not call SaveChanges.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("PcAmericaDbContext is read-only. Do not call SaveChangesAsync.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("PcAmericaDbContext is read-only. Do not call SaveChangesAsync.");
}
