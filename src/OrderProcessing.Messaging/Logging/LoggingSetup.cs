using System.Globalization;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Formatting.Compact;

namespace OrderProcessing.Messaging.Logging;

/// <summary>
/// One logging configuration, used identically by both services.
///
/// Shared deliberately: the whole value of the correlation id is that a single query returns the
/// story from both processes, and that only works if they agree on the format and on the property
/// names. Two services configured separately drift, and the drift is invisible until the day you
/// need to trace something across them.
/// </summary>
public static class LoggingSetup
{
    /// <summary>The property every log line carries so one order can be followed end to end.</summary>
    public const string CorrelationIdProperty = "CorrelationId";

    public static IHostApplicationBuilder AddStructuredLogging(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSerilog((services, configuration) =>
        {
            configuration
                // appsettings can override levels per namespace without a rebuild, which is what you
                // want at 2am. Everything below is the default, not the last word.
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()

                // Both services stamp their own name, because in aggregated logs "which process said
                // this" is the first question and the container name is not always available.
                .Enrich.WithProperty("Service", serviceName);

            if (builder.Environment.IsDevelopment())
            {
                // Readable at a terminal: a human is looking at this one.
                // Invariant culture, not the machine's. A developer in Stockholm and one in London
                // reading the same log should see the same timestamps and numbers.
                configuration.WriteTo.Console(
                    // A plain output template renders a property that is absent as nothing, which
                    // is exactly what is wanted here - startup lines carry no correlation id and
                    // should not show an empty pair of brackets. Note that conditionals ({#if ...})
                    // are Serilog.Expressions syntax and are NOT supported here: written in a plain
                    // template they are emitted literally, which is a mistake that looks like a
                    // logging bug rather than a template bug.
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {CorrelationId}{NewLine}{Exception}",
                    formatProvider: CultureInfo.InvariantCulture);
            }
            else
            {
                // One JSON object per line, to stdout. Container runtimes collect stdout and nothing
                // else, and a log collector can index every property rather than regex a template
                // back apart. Writing to a file inside a container is how logs get lost on restart.
                configuration.WriteTo.Console(new CompactJsonFormatter());
            }
        });

        return builder;
    }
}
