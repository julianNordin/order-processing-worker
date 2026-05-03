using System.Text.Json;
using System.Text.Json.Serialization;
using OrderProcessing.Contracts;

namespace OrderProcessing.UnitTests;

/// <summary>
/// The contract is the only thing the publisher and the consumer both depend on, and it is the one
/// place where a mistake is invisible at compile time on both sides at once. These tests pin the
/// wire format itself, not the C# shape: renaming a property is a source-compatible change that
/// breaks every message already sitting in a queue.
/// </summary>
public class OrderPlacedContractTests
{
    private static OrderPlaced AnOrder() => new()
    {
        SchemaVersion = MessageContracts.CurrentSchemaVersion,
        OrderId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
        CustomerEmail = "buyer@example.com",
        Total = 41.97m,
        Lines =
        [
            new OrderLine { Sku = "SKU-1", Description = "Blue widget", Quantity = 3, UnitPrice = 13.99m },
        ],
    };

    [Fact]
    public void Round_trips_without_losing_anything()
    {
        var original = AnOrder();

        var json = JsonSerializer.Serialize(original, ContractsSerializerContext.Default.OrderPlaced);
        var restored = JsonSerializer.Deserialize(json, ContractsSerializerContext.Default.OrderPlaced);

        Assert.NotNull(restored);
        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(original.OrderId, restored.OrderId);
        Assert.Equal(original.CustomerEmail, restored.CustomerEmail);
        Assert.Equal(original.Total, restored.Total);
        Assert.Equal(original.Lines, restored.Lines);
    }

    /// <summary>
    /// Why the test above compares field by field instead of writing Assert.Equal(original, restored).
    ///
    /// A record's generated Equals compares each member with EqualityComparer&lt;T&gt;.Default, and
    /// for IReadOnlyList&lt;OrderLine&gt; that is reference equality - List&lt;T&gt; does not
    /// override Equals. So two OrderPlaced values with identical contents are NOT equal, and a
    /// round-trip assertion written the obvious way fails for a reason that has nothing to do with
    /// serialization. Pinned here so the next person meets it as a documented fact rather than as a
    /// confusing red test.
    ///
    /// The individual OrderLine records DO compare by value, which is why comparing the two lists
    /// with Assert.Equal (a sequence comparison) works.
    /// </summary>
    [Fact]
    public void Record_equality_does_not_look_inside_the_lines()
    {
        var one = AnOrder();
        var two = AnOrder();

        Assert.NotEqual(one, two);                    // same contents, different list instances
        Assert.Equal(one.Lines[0], two.Lines[0]);     // but the line records themselves are equal
        Assert.Equal(one.Lines, two.Lines);           // and a sequence comparison sees through it
    }

    [Fact]
    public void Serializes_money_without_binary_floating_point_drift()
    {
        // The one that bites: 41.97 as a double serializes to 41.969999999999999. decimal is used
        // throughout the contract precisely so a receipt total is the number the customer was shown.
        var json = JsonSerializer.Serialize(AnOrder(), ContractsSerializerContext.Default.OrderPlaced);

        Assert.Contains("\"total\":41.97", json, StringComparison.Ordinal);
        Assert.Contains("\"unitPrice\":13.99", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Uses_camel_case_on_the_wire()
    {
        var json = JsonSerializer.Serialize(AnOrder(), ContractsSerializerContext.Default.OrderPlaced);

        Assert.Contains("\"schemaVersion\":", json, StringComparison.Ordinal);
        Assert.Contains("\"customerEmail\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SchemaVersion\":", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Versioning rule 2: a message from a newer publisher must survive an older consumer. During
    /// any rolling deploy there is a window where exactly that happens, and a queue can hold a
    /// message published long before the process that reads it started.
    /// </summary>
    [Fact]
    public void Ignores_a_field_a_newer_publisher_added()
    {
        var fromTheFuture = """
            {
              "schemaVersion": 1,
              "orderId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
              "customerEmail": "buyer@example.com",
              "total": 41.97,
              "lines": [
                { "sku": "SKU-1", "description": "Blue widget", "quantity": 3, "unitPrice": 13.99,
                  "giftWrapped": true }
              ],
              "promotionCode": "SUMMER"
            }
            """;

        var restored = JsonSerializer.Deserialize(fromTheFuture, ContractsSerializerContext.Default.OrderPlaced);

        Assert.NotNull(restored);
        Assert.Equal("buyer@example.com", restored.CustomerEmail);
        Assert.Equal(41.97m, restored.Total);
        Assert.Equal(3, restored.Lines[0].Quantity);
    }

    /// <summary>
    /// The positive control for the test above. An assertion that something was tolerated also
    /// passes when there was nothing to tolerate, so this proves the unknown fields are genuinely
    /// present in that JSON and that <c>UnmappedMemberHandling.Skip</c> is what makes it work -
    /// deserializing the same document with the opposite setting must throw.
    /// </summary>
    [Fact]
    public void The_unknown_fields_are_really_there_and_Skip_is_what_saves_them()
    {
        var strict = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

        var withExtraField = """
            {
              "schemaVersion": 1,
              "orderId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
              "customerEmail": "buyer@example.com",
              "total": 41.97,
              "lines": [],
              "promotionCode": "SUMMER"
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OrderPlaced>(withExtraField, strict));
    }

    [Fact]
    public void A_missing_required_field_is_an_error_rather_than_a_default()
    {
        // Silent defaulting is worse than failing: an order with no customer email would otherwise
        // reach the worker, generate a receipt addressed to nobody, and be acknowledged as a success.
        var missingEmail = """
            {
              "schemaVersion": 1,
              "orderId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
              "total": 41.97,
              "lines": []
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(missingEmail, ContractsSerializerContext.Default.OrderPlaced));
    }
}
