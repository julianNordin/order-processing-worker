using OrderProcessing.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMessaging(builder.Configuration);

var app = builder.Build();

// The worker consumes; it serves no HTTP traffic beyond the health endpoints that arrive in
// Phase 13. This placeholder exists only so the host has a route and starts cleanly.
app.MapGet("/", () => Results.Ok(new { service = "OrderProcessing.Worker" }));

await app.RunAsync();
