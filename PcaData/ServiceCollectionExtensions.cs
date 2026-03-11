using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PcaData;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PcAmericaDbContext"/> as a read-only, no-tracking context.
    ///
    /// Required appsettings.json entry:
    /// <code>
    /// "ConnectionStrings": {
    ///   "PcAmerica": "Server=.;Database=PCAmerica;User Id=...;Password=...;"
    /// }
    /// </code>
    /// ApplicationIntent=ReadOnly is appended automatically — do not add it to the
    /// connection string manually or it will be duplicated.
    /// </summary>
    public static IServiceCollection AddPcAmericaDb(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var baseConnStr = configuration.GetConnectionString("PcAmerica")
            ?? throw new InvalidOperationException(
                "Connection string 'PcAmerica' is missing from configuration.");

        // Append read-only intent — prevents accidental writes at the driver level
        // and allows SQL Server to route to a readable secondary if one exists.
        var connStr = baseConnStr.TrimEnd(';') + ";ApplicationIntent=ReadOnly";

        services.AddDbContext<PcAmericaDbContext>(opts =>
            opts.UseSqlServer(connStr)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        return services;
    }
}
