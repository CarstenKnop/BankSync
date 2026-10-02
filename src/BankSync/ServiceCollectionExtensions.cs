using BankSync.Api;
using BankSync.Core;
using BankSync.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BankSync;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers BankSync with options set in code.</summary>
    public static IServiceCollection AddBankSync(this IServiceCollection services, Action<BankSyncOptions> configure)
    {
        services.AddOptions<BankSyncOptions>().Configure(configure);
        return AddCore(services);
    }

    /// <summary>Registers BankSync bound to the "BankSync" configuration section (appsettings, environment variables BankSync__...).</summary>
    public static IServiceCollection AddBankSync(this IServiceCollection services, IConfiguration configuration, Action<BankSyncOptions>? configure = null)
    {
        var builder = services.AddOptions<BankSyncOptions>().Bind(configuration.GetSection(BankSyncOptions.SectionName));
        if (configure is not null) builder.Configure(configure);
        return AddCore(services);
    }

    private static IServiceCollection AddCore(IServiceCollection services)
    {
        services.AddOptions<BankSyncOptions>()
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();

        services.AddDbContextFactory<BankSyncDbContext>((sp, o) =>
        {
            var opt = sp.GetRequiredService<IOptions<BankSyncOptions>>().Value;
            o.UseSqlite($"Data Source={opt.DatabasePath}");
        });

        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<BankSyncOptions>>().Value;
            var pem = !string.IsNullOrWhiteSpace(o.PrivateKeyPem) ? o.PrivateKeyPem : File.ReadAllText(o.PrivateKeyPath!);
            return new JwtFactory(o.ApplicationId, pem, o.JwtLifetime, sp.GetRequiredService<TimeProvider>());
        });
        services.TryAddSingleton(sp => new SecretProtector(sp.GetRequiredService<IOptions<BankSyncOptions>>().Value.ResolveSecretKeyPath()));

        services.AddHttpClient<EnableBankingClient>();

        services.TryAddSingleton<SlotCalculator>();
        services.TryAddSingleton<QuotaGuard>();
        services.TryAddSingleton<SyncEngine>();
        services.TryAddSingleton<SyncWorkQueue>();
        services.TryAddSingleton<ConsentService>();
        services.TryAddSingleton<DatabaseInitializer>();
        services.TryAddSingleton<IBankSync, BankSyncService>();

        services.AddHostedService(sp => sp.GetRequiredService<DatabaseInitializer>());
        services.AddHostedService<SyncScheduler>();
        return services;
    }
}
