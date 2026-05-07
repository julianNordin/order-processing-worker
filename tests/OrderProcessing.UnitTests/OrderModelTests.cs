using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderProcessing.Persistence;
using OrderProcessing.Persistence.Entities;

namespace OrderProcessing.UnitTests;

/// <summary>
/// Pins the mapping decisions where the EF default is wrong for this schema, and wrong quietly.
///
/// No database is involved: EF builds the model from the provider and the configuration without ever
/// opening a connection, so these run in milliseconds anywhere. The tests that need a real Postgres
/// are the integration tier in Phase 14.
/// </summary>
public class OrderModelTests
{
    private static IModel Model { get; } = BuildModel();

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<OrderProcessingDbContext>()
            // Never connected to. The provider is needed to build the model, not to reach a server.
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new OrderProcessingDbContext(options);
        return context.Model;
    }

    [Theory]
    [InlineData(typeof(Order), "orders")]
    [InlineData(typeof(OrderLine), "order_lines")]
    public void Tables_are_named_in_snake_case(Type entity, string expectedTable)
    {
        Assert.Equal(expectedTable, Model.FindEntityType(entity)!.GetTableName());
    }

    [Fact]
    public void Columns_are_named_in_snake_case()
    {
        // Postgres folds unquoted identifiers to lower case, so a PascalCase column has to be quoted
        // in every hand-written query - including the one written during an incident.
        var order = Model.FindEntityType(typeof(Order))!;
        var storeObject = StoreObjectIdentifier.Table(order.GetTableName()!, order.GetSchema());

        Assert.Equal("customer_email", order.FindProperty(nameof(Order.CustomerEmail))!.GetColumnName(storeObject));
        Assert.Equal("placed_at", order.FindProperty(nameof(Order.PlacedAt))!.GetColumnName(storeObject));
        Assert.Equal("failure_reason", order.FindProperty(nameof(Order.FailureReason))!.GetColumnName(storeObject));
    }

    [Theory]
    [InlineData(typeof(Order), nameof(Order.Total))]
    [InlineData(typeof(OrderLine), nameof(OrderLine.UnitPrice))]
    public void Money_is_stored_as_exact_decimal(Type entity, string propertyName)
    {
        // The default mapping for decimal loses precision on some providers, and money that is
        // approximately right is money that is wrong. 18,2 is declared rather than inherited.
        var property = Model.FindEntityType(entity)!.FindProperty(propertyName)!;

        Assert.Equal(18, property.GetPrecision());
        Assert.Equal(2, property.GetScale());
    }

    [Fact]
    public void Status_is_stored_as_text_rather_than_as_a_number()
    {
        // An integer column reads as "status = 3" during an incident, and renumbering the enum
        // later reinterprets every row already written.
        var order = Model.FindEntityType(typeof(Order))!;
        var status = order.FindProperty(nameof(Order.Status))!;
        var storeObject = StoreObjectIdentifier.Table(order.GetTableName()!, order.GetSchema());

        // Assert on the column type the provider will actually emit rather than on the presence of a
        // converter object. EF can express this mapping either through an explicit value converter or
        // through the type mapping, depending on how it was configured - and it is the SQL type that
        // the operator reading the table at 2am actually sees.
        Assert.Equal("character varying(20)", status.GetColumnType(storeObject));
    }

    [Fact]
    public void The_order_id_is_chosen_by_the_application_not_the_database()
    {
        // It has to be known before the row is written: the same id goes into the 202 response and
        // into the outbox message, in one transaction. A database-generated key cannot do that.
        var id = Model.FindEntityType(typeof(Order))!.FindProperty(nameof(Order.Id))!;

        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);
    }

    [Fact]
    public void Deleting_an_order_takes_its_lines_with_it()
    {
        var foreignKey = Model.FindEntityType(typeof(OrderLine))!.GetForeignKeys().Single();

        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }
}
