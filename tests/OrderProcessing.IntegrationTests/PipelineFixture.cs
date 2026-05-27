using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Persistence;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

using ApiProgram = OrderProcessing.Api.Program;
using WorkerProgram = OrderProcessing.Worker.Program;

namespace OrderProcessing.IntegrationTests;

/// <summary>
/// A real broker, a real database, and both services running against them.
///
/// Everything below the HTTP call is genuine. That is the point of this tier: the unit tests can
/// prove that <c>RetryDecision</c> picks the right wait queue, but only a real broker can prove that
/// the queue exists, is bound to the exchange the publisher uses, has the TTL that was asked for,
/// and dead-letters back to somewhere the consumer is listening. Every one of those is a silent
/// failure in production and invisible to a mock.
/// </summary>
public sealed class PipelineFixture : IAsyncLifetime
{
    // The same image tags the real compose file uses. Testing against a different Postgres major
    // than production runs would make this tier prove the wrong thing.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("orderprocessing")
        .WithUsername("orderprocessing")
        .WithPassword("integration-tests")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4.2.6-management")
        .WithUsername("orderprocessing")
        .WithPassword("integration-tests")
        .Build();

    private WebApplicationFactory<ApiProgram>? _api;
    private WebApplicationFactory<WorkerProgram>? _worker;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Faults configured for the whole run; individual tests target them by email address.</summary>
    public const string TransientFailureMarker = "transient-fail";

    public const string PermanentFailureMarker = "permanent-fail";

    /// <summary>Fails on every attempt, so retry exhaustion can be reached.</summary>
    public const string AlwaysFailMarker = "always-fail";

    public async Task InitializeAsync()
    {
        // Started together: they have nothing to do with each other and serialising costs ~10s.
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync()).ConfigureAwait(false);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:OrderProcessing"] = _postgres.GetConnectionString(),
            ["RabbitMq:Host"] = _rabbitMq.Hostname,
            ["RabbitMq:Port"] = _rabbitMq.GetMappedPublicPort(5672).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RabbitMq:UserName"] = "orderprocessing",
            ["RabbitMq:Password"] = "integration-tests",
            ["RabbitMq:PrefetchCount"] = "5",

            // A short poll so the tests are not mostly spent waiting for the outbox to notice.
            ["Outbox:PollInterval"] = "00:00:00.200",

            // Both fault switches are armed for the whole run. A test opts in by using the matching
            // address, which keeps the fixture single and the tests independent of each other.
            ["Faults:FailTransientlyForEmailContaining"] = TransientFailureMarker,
            ["Faults:FailPermanentlyForEmailContaining"] = PermanentFailureMarker,
            ["Faults:AlwaysFailTransientlyForEmailContaining"] = AlwaysFailMarker,
            ["Faults:SucceedAfterAttempts"] = "1",
        };

        _api = new PipelineHost<ApiProgram>(settings);
        _worker = new PipelineHost<WorkerProgram>(settings);

        Client = _api.CreateClient();

        await using (var scope = _api.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();
            await database.Database.MigrateAsync().ConfigureAwait(false);
        }

        // Force the worker's host to build so its hosted services actually start. CreateClient is
        // the documented way to do that - a WebApplicationFactory is lazy until something asks it
        // for a client, and a worker nobody asked for a client from consumes nothing.
        _worker.CreateClient().Dispose();

        // Wait for the consumer to be attached rather than assuming. Testcontainers reports a
        // container "running" well before RabbitMQ accepts AMQP, and the services retry through
        // that gap - so readiness here means "the pipeline is actually consuming", not "the
        // container started".
        await WaitUntilAsync(
            async () =>
            {
                var response = await Client.GetAsync(new Uri("/health/ready", UriKind.Relative)).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            },
            TimeSpan.FromMinutes(2),
            "the API never became ready").ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_worker is not null) { await _worker.DisposeAsync().ConfigureAwait(false); }
        if (_api is not null) { await _api.DisposeAsync().ConfigureAwait(false); }
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    public IServiceScope CreateScope() => _api!.Services.CreateScope();

    public T GetRequiredService<T>() where T : notnull => _worker!.Services.GetRequiredService<T>();

    /// <summary>
    /// Polls until a condition holds or a deadline passes.
    ///
    /// <b>There is not a single fixed sleep in this suite, and that is deliberate.</b> A test that
    /// waits a hard-coded two seconds is either slower than it needs to be or flaky on a loaded
    /// machine, and usually manages both. Polling for the state that actually matters is faster in
    /// the common case and honest in the slow one - and when it does fail, the message says what was
    /// being waited for rather than merely that an assertion did not hold.
    /// </summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var clock = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(50);

        while (clock.Elapsed < timeout)
        {
            try
            {
                if (await condition().ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The service may not be listening yet. Keep waiting; the deadline is the guard.
            }

            await Task.Delay(delay).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 500));
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#}s waiting for {description}.");
    }

    private sealed class PipelineHost<TEntryPoint>(Dictionary<string, string?> settings) : WebApplicationFactory<TEntryPoint>
        where TEntryPoint : class
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }
    }
}

/// <summary>
/// One broker and one database for the whole assembly. Starting a pair of containers per test class
/// would add tens of seconds per class for no isolation benefit - the tests keep themselves apart by
/// using their own order ids, not by having their own infrastructure.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SharedPipeline : ICollectionFixture<PipelineFixture>
{
    public const string Name = "pipeline";
}
