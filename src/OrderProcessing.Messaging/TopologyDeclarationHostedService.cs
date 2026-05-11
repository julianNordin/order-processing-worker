using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderProcessing.Messaging;

/// <summary>
/// Declares the topology in the background, retrying until it succeeds.
///
/// It is a BackgroundService rather than a plain IHostedService, and that choice is load-bearing.
/// An IHostedService whose StartAsync throws stops the host from starting at all - so declaring the
/// topology synchronously would mean the API refuses to boot whenever the broker is down.
///
/// That would defeat the entire point of the outbox. The API is supposed to accept orders and answer
/// 202 while the broker is unreachable, with the messages accumulating in the database until it
/// comes back. An API that will not start without the broker has simply moved the outage earlier.
///
/// The cost is that a publish can happen before the topology exists. That is handled rather than
/// ignored: publishes are mandatory, so an unroutable message raises rather than vanishing, and the
/// outbox row stays unpublished and is retried once the declaration lands.
/// </summary>
internal sealed class TopologyDeclarationHostedService(
    TopologyDeclarer declarer,
    ILogger<TopologyDeclarationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await declarer.DeclareAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                MessagingLog.TopologyDeclarationFailed(logger, ex);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
