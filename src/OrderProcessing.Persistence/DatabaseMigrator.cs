using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderProcessing.Persistence;

/// <summary>
/// Applies pending migrations at startup, when configured to.
///
/// Off by default, and on in Compose. Without it, "clone the repository and run docker compose up"
/// produces four healthy containers and an empty database, which is a worse first experience than an
/// obvious failure.
///
/// <b>This is a convenience appropriate to this scale, not a general recommendation.</b> With
/// several replicas starting at once it relies on EF taking a lock so that only one applies the
/// migration; and a migration that fails takes the service down with it, which is exactly when you
/// would rather have applied it deliberately and separately. At any real size this belongs in a
/// deployment step or a job that runs once, not in the application's own startup path. Said here
/// rather than left for someone to discover.
/// </summary>
public sealed class DatabaseMigrator(IServiceScopeFactory scopeFactory, ILogger<DatabaseMigrator> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

        var pending = (await database.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        PersistenceLog.ApplyingMigrations(logger, pending.Length);
        await database.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        PersistenceLog.MigrationsApplied(logger, pending.Length);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static partial class PersistenceLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Applying {Count} pending migration(s)")]
    public static partial void ApplyingMigrations(ILogger logger, int count);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Applied {Count} migration(s)")]
    public static partial void MigrationsApplied(ILogger logger, int count);
}
