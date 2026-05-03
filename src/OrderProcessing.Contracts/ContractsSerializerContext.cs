using System.Text.Json.Serialization;

namespace OrderProcessing.Contracts;

/// <summary>
/// Source-generated serialization for every contract type.
///
/// Generated rather than reflection-based for three reasons: it is faster on a hot consume loop, it
/// keeps the contracts trimmable and AOT-safe, and - most usefully here - it makes the set of
/// serializable types explicit. A contract type that nobody remembered to register fails at build
/// time rather than at three in the morning on a message that will not deserialize.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    // Unknown members are ignored rather than rejected. This is versioning rule 2 expressed in
    // configuration: a message from a newer publisher has to survive an older consumer.
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(OrderLine))]
public sealed partial class ContractsSerializerContext : JsonSerializerContext
{
    // Deliberately empty. Both services serialize through the generated type metadata - for example
    // ContractsSerializerContext.Default.OrderPlaced - which is the path that uses no reflection at
    // all. Publisher and consumer sharing one context is also what stops them disagreeing about
    // casing, which is the same class of silent bug as disagreeing about a routing key.
}
