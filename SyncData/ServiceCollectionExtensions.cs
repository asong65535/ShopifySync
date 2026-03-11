using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SyncData;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SyncDbContext"/> against the ShopifySync database.
    ///
    /// Required appsettings.json entry:
    /// <code>
    /// "ConnectionStrings": {
    ///   "ShopifySync": "Server=...;Database=ShopifySync;User Id=...;Password=...;"
    /// }
    /// </code>
    /// </summary>
    public static IServiceCollection AddSyncDb(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connStr = configuration.GetConnectionString("ShopifySync")
            ?? throw new InvalidOperationException(
                "Connection string 'ShopifySync' is missing from configuration.");

        services.AddDbContext<SyncDbContext>(opts =>
            opts.UseSqlServer(connStr));

        return services;
    }
}
