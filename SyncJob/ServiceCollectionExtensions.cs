using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SyncJob.Shopify;

namespace SyncJob;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers SyncService and all its dependencies.
    ///
    /// Required configuration keys:
    ///   ConnectionStrings:PcAmerica    — read-only PCAmerica DB
    ///   ConnectionStrings:ShopifySync  — read/write sync DB
    ///   Shopify:StoreUrl               — e.g. "store.myshopify.com"
    ///   Shopify:AccessToken            — Admin API token
    /// </summary>
    public static IServiceCollection AddSyncJob(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpClient<ShopifyClient>();
        services.AddScoped<SyncOrchestrator>();
        services.AddSingleton<SyncService>();

        return services;
    }
}
