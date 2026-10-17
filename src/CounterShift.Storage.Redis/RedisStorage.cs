using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CounterShift.Storage.Redis;

public static class RedisStorage
{
    /// <summary>
    /// Builds connection options that never block startup: if Redis is down, the app still starts and
    /// commands fail fast with StorageUnavailableException. During most of a migration the API doesn't
    /// need Redis yet, so Redis being down must not take the API down with it.
    /// </summary>
    public static ConfigurationOptions Options(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;  // keep retrying in the background instead of throwing at startup
        options.ConnectTimeout = 2000;
        options.SyncTimeout = 2000;
        options.AsyncTimeout = 2000;          // a slow store must not hold requests hostage
        return options;
    }

    /// <summary>Registers the Redis backend. The connection is created on first use.</summary>
    public static IServiceCollection AddRedisCounterBackend(this IServiceCollection services, Func<IServiceProvider, string?> connectionString)
    {
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var cs = connectionString(sp);
            if (string.IsNullOrWhiteSpace(cs))
            {
                throw new InvalidOperationException("The Redis connection string (ConnectionStrings:Redis) is not configured.");
            }
            return ConnectionMultiplexer.Connect(Options(cs));
        });
        services.AddSingleton(sp => new RedisCounterBackend(sp.GetRequiredService<IConnectionMultiplexer>()));
        services.AddSingleton<ICounterBackend>(sp => sp.GetRequiredService<RedisCounterBackend>());
        return services;
    }
}
