using System.Security.Cryptography;
using BankSync.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BankSync.Tests;

/// <summary>Builds a service provider with BankSync wired to <see cref="FakeEnableBanking"/>, a temp SQLite file and a fake clock.</summary>
public sealed class TestHost : IDisposable
{
    public static readonly string TestPem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();

    public FakeEnableBanking Fake { get; } = new();
    public FakeTimeProvider Clock { get; }
    public ServiceProvider Services { get; }
    public IBankSync Bank => Services.GetRequiredService<IBankSync>();
    public string Directory { get; }

    public TestHost(Action<BankSyncOptions>? configure = null, DateTimeOffset? now = null, Action<IServiceCollection>? configureServices = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "banksync-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.FromHours(2)));
        Fake.Now = Clock.GetUtcNow;

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddBankSync(o =>
        {
            o.ApplicationId = "11111111-2222-3333-4444-555555555555";
            o.PrivateKeyPem = TestPem;
            o.RedirectUrl = "https://localhost/callback";
            o.DatabasePath = Path.Combine(Directory, "test.db");
            o.EnableScheduler = false;
            o.MaxSlotJitter = TimeSpan.Zero;
            configure?.Invoke(o);
        });
        services.AddHttpClient<EnableBankingClient>().ConfigurePrimaryHttpMessageHandler(() => Fake);
        configureServices?.Invoke(services);
        Services = services.BuildServiceProvider();
    }

    public PsuContext Psu { get; } = new("203.0.113.10", "Mozilla/5.0 (test)", "da");

    public DateOnly Today => DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);

    /// <summary>Runs the consent flow end to end against the fake.</summary>
    public async Task<ConsentStatus> ConnectAsync(bool withPsu = true)
    {
        await Bank.BeginConsentAsync(withPsu ? Psu : null);
        var state = await PendingStateAsync();
        return await Bank.CompleteConsentAsync("code-123", state, withPsu ? Psu : null);
    }

    private async Task<string> PendingStateAsync()
    {
        var factory = Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Data.BankSyncDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return db.PendingAuthorizations.Single().State;
    }

    public void Dispose()
    {
        Services.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best effort */ }
    }
}
