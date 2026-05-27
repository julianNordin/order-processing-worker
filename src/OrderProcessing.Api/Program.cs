using OrderProcessing.Api.Admin;
using OrderProcessing.Api.Orders;
using OrderProcessing.Api.Outbox;
using OrderProcessing.Messaging;
using OrderProcessing.Messaging.Logging;
using Serilog;
using OrderProcessing.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddStructuredLogging("OrderProcessing.Api");

builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration);
builder.Services.AddOutboxPublisher(builder.Configuration);

// Every failure this API produces is a problem+json document, including the ones it did not write
// itself - an unhandled exception would otherwise leak a stack trace in Development and an empty
// body in Production, and neither is something a client can act on.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderProcessingDbContext>("database", tags: [HealthEndpoints.ReadinessTag])
    .AddBrokerCheck()
    .AddCheck<OutboxBacklogHealthCheck>("outbox", tags: [HealthEndpoints.ReadinessTag]);

builder.Services.AddProblemDetails();

var app = builder.Build();

// One line per request instead of the framework's several, and - more usefully - the line carries
// the trace id that every downstream log entry is correlated on.
app.UseSerilogRequestLogging();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOrderEndpoints();
app.MapAdminEndpoints();
app.MapHealthEndpoints();

await app.RunAsync();

namespace OrderProcessing.Api
{
    /// <summary>
    /// Named so that WebApplicationFactory can find the entry point.
    ///
    /// A program written with top-level statements compiles to an INTERNAL Program class, which the
    /// test host cannot reach. Declaring the partial here is the documented way round it, and is
    /// narrower than making the whole assembly visible to the test project.
    ///
    /// It is namespaced because both services would otherwise contribute a type called Program, and
    /// a test project referencing both cannot then say which one it means.
    /// </summary>
    public partial class Program;
}
