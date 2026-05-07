namespace OrderProcessing.Persistence.Entities;

/// <summary>
/// Where an order is in the pipeline.
///
/// Stored as text rather than as an integer. An integer column saves a few bytes and costs you the
/// ability to read your own database during an incident: "status = 3" tells you nothing at 2am, and
/// renumbering the enum later silently reinterprets every existing row.
/// </summary>
public enum OrderStatus
{
    /// <summary>Written down and acknowledged to the customer. Nothing has been processed yet.</summary>
    Accepted,

    /// <summary>A worker has picked the message up and is generating the receipt.</summary>
    Processing,

    /// <summary>The receipt exists and can be downloaded.</summary>
    Completed,

    /// <summary>Every retry was used up, or the message could never succeed. See the failure reason.</summary>
    Failed,
}
