using Microsoft.EntityFrameworkCore;
using OrderProcessing.Persistence.Entities;

namespace OrderProcessing.Persistence;

/// <summary>
/// The one schema both services share.
///
/// Sharing a database between two services is normally a smell, and it is a deliberate choice here:
/// the worker writes receipts and the API reads them, so they have to agree on the shape. In a
/// system with more than one team this would be an internal API instead. At this size, one schema
/// owned by one project is the honest arrangement, and it is written down in docs/architecture.md
/// rather than left as something a reader has to infer.
/// </summary>
public class OrderProcessingDbContext(DbContextOptions<OrderProcessingDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(order =>
        {
            order.HasKey(o => o.Id);

            // No database-generated default. The id is chosen by the API before the row is written,
            // because it goes into the 202 response and into the outbox message in the same
            // transaction - all three have to agree, and only the application can guarantee that.
            order.Property(o => o.Id).ValueGeneratedNever();

            order.Property(o => o.CustomerEmail).HasMaxLength(320).IsRequired();

            // numeric(18,2), not double precision. Money in binary floating point is how a receipt
            // ends up saying 41.969999999999999.
            order.Property(o => o.Total).HasPrecision(18, 2);

            // Stored as text - see the comment on OrderStatus.
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            order.Property(o => o.FailureReason).HasMaxLength(1000);

            order.HasMany(o => o.Lines)
                .WithOne()
                .HasForeignKey(l => l.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // The API lists recent orders and the worker looks orders up by state. Both want this.
            order.HasIndex(o => o.Status);
            order.HasIndex(o => o.PlacedAt);
        });

        modelBuilder.Entity<OrderLine>(line =>
        {
            line.HasKey(l => l.Id);
            line.Property(l => l.Sku).HasMaxLength(64).IsRequired();
            line.Property(l => l.Description).HasMaxLength(500).IsRequired();
            line.Property(l => l.UnitPrice).HasPrecision(18, 2);
        });
    }
}
