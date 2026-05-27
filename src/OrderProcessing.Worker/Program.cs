using OrderProcessing.Messaging;
using OrderProcessing.Messaging.Logging;
using OrderProcessing.Persistence;
using OrderProcessing.Worker.Consuming;
using OrderProcessing.Worker.Receipts;
using QuestPDF.Infrastructure;

// QuestPDF refuses to render anything until a licence is declared, and it throws on the FIRST
// render rather than at startup - so without this line the failure appears as a dead-lettered
// message rather than as a configuration problem. Community is the correct licence here.
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.AddStructuredLogging("OrderProcessing.Worker");

builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration);

builder.Services.AddOptions<FaultInjectionOptions>()
    .Bind(builder.Configuration.GetSection(FaultInjectionOptions.SectionName));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderProcessingDbContext>("database", tags: [HealthEndpoints.ReadinessTag])
    .AddBrokerCheck();

builder.Services.AddSingleton<IReceiptRenderer, ReceiptRenderer>();
builder.Services.AddScoped<OrderPlacedHandler>();
builder.Services.AddHostedService<OrderConsumer>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "OrderProcessing.Worker" }));
app.MapHealthEndpoints();

await app.RunAsync();

namespace OrderProcessing.Worker
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
