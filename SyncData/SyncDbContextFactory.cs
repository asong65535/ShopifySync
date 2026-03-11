using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SyncData;

/// <summary>
/// Design-time factory used by EF Core tooling (migrations, scaffolding).
/// Not used at runtime.
/// </summary>
internal class SyncDbContextFactory : IDesignTimeDbContextFactory<SyncDbContext>
{
    public SyncDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<SyncDbContext>()
            .UseSqlServer("Server=localhost,1433;Database=ShopifySync;User Id=sa;Password=Passw0rd;TrustServerCertificate=True;")
            .Options;

        return new SyncDbContext(opts);
    }
}
