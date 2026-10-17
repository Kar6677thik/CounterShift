using CounterShift.Storage.Migration;
using Microsoft.Extensions.DependencyInjection;

namespace CounterShift.Storage;

public static class StorageRegistration
{
    /// <summary>
    /// Registers the facade and the migration machinery. Backends register themselves separately
    /// (AddPostgresCounterBackend, AddRedisCounterBackend), and the mode decides how they're used.
    /// </summary>
    public static IServiceCollection AddCounterStore(this IServiceCollection services)
    {
        services.AddMetrics();
        services.AddSingleton<StorageMetrics>();
        services.AddSingleton<StorageModeProvider>();
        services.AddSingleton<ShadowComparer>();
        services.AddHostedService(sp => sp.GetRequiredService<ShadowComparer>());
        services.AddSingleton<BackfillRunner>();
        services.AddSingleton<CounterStore>();
        return services;
    }
}
