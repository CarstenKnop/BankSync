using BankSync.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankSync.Core;

/// <summary>Creates the SQLite database and switches it to WAL mode. Runs once, on first use or at host start.</summary>
internal sealed class DatabaseInitializer(
    IDbContextFactory<BankSyncDbContext> dbFactory,
    IOptions<BankSyncOptions> options,
    ILogger<DatabaseInitializer> log) : IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_done) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_done) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(options.Value.DatabasePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Database.EnsureCreatedAsync(ct);
            try { await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct); }
            catch (Exception ex) { log.LogDebug(ex, "Could not set WAL mode (in-memory database?)"); }
            _done = true;
            log.LogInformation("BankSync database ready at {Path}", options.Value.DatabasePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => EnsureInitializedAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
