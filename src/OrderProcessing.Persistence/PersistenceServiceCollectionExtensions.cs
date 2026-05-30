using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OrderProcessing.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "OrderProcessing";

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"No connection string named '{ConnectionStringName}'. Set " +
                $"ConnectionStrings__{ConnectionStringName} in the environment - it is never committed.");

        services.AddDbContext<OrderProcessingDbContext>(options => options
            .UseNpgsql(connectionString)
            // snake_case throughout, so the schema reads like SQL rather than like C#. Postgres
            // folds unquoted identifiers to lower case, so PascalCase table names have to be quoted
            // in every hand-written query - which is exactly the query you write during an incident.
            .UseSnakeCaseNamingConvention());

        // Opt-in, so a deployed system can keep schema changes as a deliberate step while
        // `docker compose up` still produces something that works from nothing.
        if (bool.TryParse(configuration["Database:MigrateOnStartup"], out var migrateOnStartup) && migrateOnStartup)
        {
            services.AddHostedService<DatabaseMigrator>();
        }

        // Injected rather than calling DateTimeOffset.UtcNow at the point of use, so that time is a
        // dependency the tests can control instead of an ambient fact they have to tolerate.
        services.TryAddSingletonTimeProvider();

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
