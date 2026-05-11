
namespace OrderProcessing.Api.Outbox;

public static class OutboxServiceCollectionExtensions
{
    public static IServiceCollection AddOutboxPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHostedService<OutboxPublisher>();

        return services;
    }
}
