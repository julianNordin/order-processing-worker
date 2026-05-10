using OrderProcessing.Api.Orders;

namespace OrderProcessing.UnitTests;

/// <summary>
/// The validator is the only thing standing between a caller's JSON and a row in the database, so
/// what it rejects matters as much as what it accepts. These tests pin both, and in particular pin
/// the error KEYS - a client builds its form highlighting from those, so "Lines[1].Quantity"
/// becoming "Quantity" is a breaking change even though nothing about the C# changed.
/// </summary>
public class OrderRequestValidatorTests
{
    private static PlaceOrderRequest AValidRequest(params PlaceOrderLine[] lines) => new()
    {
        CustomerEmail = "buyer@example.com",
        Lines = lines.Length > 0 ? lines : [AValidLine()],
    };

    private static PlaceOrderLine AValidLine() => new()
    {
        Sku = "SKU-1",
        Description = "Blue widget",
        Quantity = 3,
        UnitPrice = 13.99m,
    };

    [Fact]
    public void Accepts_a_well_formed_order()
    {
        Assert.True(OrderRequestValidator.IsValid(AValidRequest(), out var errors));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("two@at@signs.com")]
    public void Rejects_an_address_that_could_not_receive_a_receipt(string email)
    {
        var request = AValidRequest() with { CustomerEmail = email };

        Assert.False(OrderRequestValidator.IsValid(request, out var errors));
        Assert.Contains(nameof(PlaceOrderRequest.CustomerEmail), errors.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void Accepts_an_address_with_no_dot_in_it()
    {
        // Documenting real behaviour rather than an assumption. [EmailAddress] is deliberately
        // permissive: it wants an @ with something either side and little more. "buyer@localhost"
        // and "user@intranet" are genuine deliverable addresses, so rejecting them would be wrong.
        //
        // Chasing stricter syntax validation is a well-known dead end - the grammar in RFC 5322 is
        // far more permissive than the regexes people write for it, and the only test that actually
        // proves an address works is sending mail to it. This layer is here to catch typos, not to
        // certify deliverability.
        var request = AValidRequest() with { CustomerEmail = "buyer@localhost" };

        Assert.True(OrderRequestValidator.IsValid(request, out _));
    }

    [Fact]
    public void Rejects_an_order_with_no_lines()
    {
        // An order with nothing in it would produce an empty receipt and a total of zero, and
        // would look like a success the whole way through.
        var request = new PlaceOrderRequest { CustomerEmail = "buyer@example.com", Lines = [] };

        Assert.False(OrderRequestValidator.IsValid(request, out var errors));
        Assert.Contains(nameof(PlaceOrderRequest.Lines), errors.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void Names_the_line_that_is_wrong_rather_than_the_order()
    {
        var request = AValidRequest(
            AValidLine(),
            new PlaceOrderLine { Sku = "", Description = "bad", Quantity = 0, UnitPrice = -5m });

        Assert.False(OrderRequestValidator.IsValid(request, out var errors));

        // The index is the point. A client highlights the offending row from this key.
        Assert.Contains("Lines[1].Sku", errors.Keys, StringComparer.Ordinal);
        Assert.Contains("Lines[1].Quantity", errors.Keys, StringComparer.Ordinal);
        Assert.Contains("Lines[1].UnitPrice", errors.Keys, StringComparer.Ordinal);

        // ...and the good line must not be blamed for its neighbour.
        Assert.DoesNotContain(errors.Keys, key => key.StartsWith("Lines[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_every_problem_at_once_rather_than_the_first()
    {
        // A caller fixing one field at a time across five round trips is a caller who gives up.
        var request = new PlaceOrderRequest
        {
            CustomerEmail = "nonsense",
            Lines = [new PlaceOrderLine { Sku = "", Description = "", Quantity = 0, UnitPrice = -1m }],
        };

        Assert.False(OrderRequestValidator.IsValid(request, out var errors));
        Assert.True(errors.Count >= 4, $"expected several errors, got {errors.Count}");
    }

    [Fact]
    public void A_free_line_is_allowed_but_a_negative_one_is_not()
    {
        // Zero is a legitimate price - a promotional item on a real order. Negative is not, because
        // it would let a caller reduce the total of an order they do not otherwise control.
        var free = AValidRequest(AValidLine() with { UnitPrice = 0m });
        var negative = AValidRequest(AValidLine() with { UnitPrice = -0.01m });

        Assert.True(OrderRequestValidator.IsValid(free, out _));
        Assert.False(OrderRequestValidator.IsValid(negative, out _));
    }
}
