using OrderProcessing.Messaging;
using OrderProcessing.Persistence;
using OrderProcessing.Worker.Consuming;
using OrderProcessing.Worker.Receipts;
using QuestPDF.Infrastructure;

// QuestPDF refuses to render anything until a licence is declared, and it throws on the FIRST
// render rather than at startup - so without this line the failure appears as a dead-lettered
// message rather than as a configuration problem. Community is the correct licence here.
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration);

builder.Services.AddSingleton<IReceiptRenderer, ReceiptRenderer>();
builder.Services.AddScoped<OrderPlacedHandler>();
builder.Services.AddHostedService<OrderConsumer>();

var app = builder.Build();

// The worker consumes; the health endpoints it will actually need arrive in Phase 13.
app.MapGet("/", () => Results.Ok(new { service = "OrderProcessing.Worker" }));

await app.RunAsync();
