using System.ComponentModel.DataAnnotations;

namespace OrderProcessing.Api.Orders;

/// <summary>
/// Validates a place-order request, producing the errors dictionary a problem+json
/// ValidationProblem expects.
///
/// Written by hand rather than left to a filter for one reason: the nested lines have to be
/// validated too, and the caller deserves to be told WHICH line was wrong. "Lines[2].Quantity" is
/// actionable; a blanket complaint about the request is not.
///
/// It lives outside the endpoint so that it can be tested without a host, a database or a broker.
/// </summary>
public static class OrderRequestValidator
{
    public static bool IsValid(PlaceOrderRequest request, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(request);

        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        Collect(found, request, string.Empty);

        for (var i = 0; i < request.Lines.Count; i++)
        {
            Collect(found, request.Lines[i], $"Lines[{i}]");
        }

        errors = found.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        return errors.Count == 0;
    }

    private static void Collect(Dictionary<string, List<string>> found, object instance, string prefix)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);

        foreach (var result in results)
        {
            foreach (var member in result.MemberNames.DefaultIfEmpty(string.Empty))
            {
                var key = string.IsNullOrEmpty(prefix) ? member : $"{prefix}.{member}";

                if (!found.TryGetValue(key, out var messages))
                {
                    found[key] = messages = [];
                }

                messages.Add(result.ErrorMessage ?? "Invalid.");
            }
        }
    }
}
