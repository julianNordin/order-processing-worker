using Microsoft.Extensions.Hosting;

namespace OrderProcessing.Messaging;

/// <summary>
/// Declares the topology once, during startup, before anything tries to publish or consume.
///
/// Registered by both services. Whichever starts first creates the exchanges and queues; the other
/// declares the identical thing and the broker treats it as a no-op. Doing it at startup rather than
/// lazily on first use means a topology mistake is a startup failure - loud, immediate, and attached
/// to the process that caused it - instead of a message that quietly goes nowhere an hour later.
/// </summary>
internal sealed class TopologyDeclarationHostedService(TopologyDeclarer declarer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => declarer.DeclareAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
