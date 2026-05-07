using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderProcessing.Persistence;

/// <summary>
/// Lets `dotnet ef` construct a DbContext without booting either service.
///
/// Without this the tooling has to start the API's host to find the registration, which drags the
/// broker connection and every other startup dependency into what should be a schema operation. The
/// connection string here is only ever used by design-time tooling - migrations describe a schema,
/// they do not need a reachable database to be generated.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrderProcessingDbContext>
{
    public OrderProcessingDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__OrderProcessing")
            ?? "Host=localhost;Port=5432;Database=orderprocessing;Username=orderprocessing;Password=local-development-only";

        var options = new DbContextOptionsBuilder<OrderProcessingDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new OrderProcessingDbContext(options);
    }
}
