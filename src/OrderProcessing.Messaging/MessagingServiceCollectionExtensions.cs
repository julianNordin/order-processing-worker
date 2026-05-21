using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OrderProcessing.Messaging;

/// <summary>
/// Registration for both services. The API and the worker call this identically, which is what
/// keeps their view of the broker the same.
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateDataAnnotations()
            // Validate at startup rather than on first use. A missing password should stop the
            // process immediately, not surface as a failed publish some minutes into a shift.
            .ValidateOnStart();

        services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();
        services.AddSingleton<TopologyDeclarer>();
        services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();
        services.AddSingleton<IQueueInspector, QueueInspector>();
        services.AddHostedService<TopologyDeclarationHostedService>();

        return services;
    }
}
