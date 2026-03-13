using Microsoft.EntityFrameworkCore;
using SyncData.Models;

namespace SyncData;

public class SyncDbContext : DbContext
{
    public SyncDbContext(DbContextOptions<SyncDbContext> options)
        : base(options) { }

    public DbSet<ProductSyncMap> ProductSyncMap { get; set; } = null!;
    public DbSet<SyncUnmatched> SyncUnmatched { get; set; } = null!;
    public DbSet<SyncState> SyncState { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<ProductSyncMap>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PcaItemNum).HasMaxLength(50).IsRequired();
            e.Property(x => x.PcaUpc).HasMaxLength(100);
            e.Property(x => x.LastKnownQty).HasColumnType("decimal(18,4)");
            e.Property(x => x.LastKnownPcaQty).HasColumnType("decimal(18,4)");
            e.Property(x => x.LastKnownShopifyQty).HasColumnType("decimal(18,4)");
            e.Property(x => x.LastSyncedAt).HasColumnType("datetime2");
        });

        mb.Entity<SyncUnmatched>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PcaItemNum).HasMaxLength(50).IsRequired();
            e.Property(x => x.PcaItemName).HasMaxLength(255);
            e.Property(x => x.PcaUpc).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.LoggedAt).HasColumnType("datetime2");
        });

        mb.Entity<SyncState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); // singleton row — Id is always 1
            e.Property(x => x.LastPolledAt).HasColumnType("datetime2");
        });
    }
}
