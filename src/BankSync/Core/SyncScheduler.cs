using System.Threading.Channels;
using BankSync.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankSync.Core;

internal enum SyncWorkKind { Backfill }

internal sealed record SyncWorkItem(SyncWorkKind Kind, PsuContext? Psu, DateTimeOffset PsuValidUntil);

internal sealed class SyncWorkQueue
{
    private readonly Channel<SyncWorkItem> _channel = Channel.CreateUnbounded<SyncWorkItem>(new UnboundedChannelOptions { SingleReader = true });
    public ChannelReader<SyncWorkItem> Reader => _channel.Reader;
    public void Enqueue(SyncWorkItem item) => _channel.Writer.TryWrite(item);
}

/// <summary>
/// Background loop: runs the incremental sync at each configured slot, catches up one missed slot after a
/// restart, and processes backfill requests queued by the consent flow.
/// </summary>
internal sealed class SyncScheduler(
    SyncWorkQueue queue,
    SyncEngine engine,
    DatabaseInitializer initializer,
    SlotCalculator slots,
    IDbContextFactory<BankSyncDbContext> dbFactory,
    IOptions<BankSyncOptions> options,
    TimeProvider time,
    ILogger<SyncScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableScheduler)
        {
            log.LogInformation("BankSync scheduler disabled by configuration");
            return;
        }

        await initializer.EnsureInitializedAsync(stoppingToken);
        log.LogInformation("BankSync scheduler started; slots {Slots} ({TimeZone})", string.Join(",", slots.Slots), slots.TimeZone.Id);

        try
        {
            await CatchUpAsync(stoppingToken);
            await RunPendingBackfillAsync(null, stoppingToken);

            // The queue allows one reader, so there must be at most one outstanding wait on it. It is kept across
            // loop iterations: a slot firing must not start a second wait while the first is still pending.
            Task<bool>? waitForItem = null;

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                var next = slots.NextSlot(now);
                var delay = next is null ? TimeSpan.FromHours(1) : next.Value - now;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

                waitForItem ??= queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var waitForSlot = Task.Delay(delay, time, delayCts.Token);
                var completed = await Task.WhenAny(waitForItem, waitForSlot);

                if (completed == waitForItem)
                {
                    await delayCts.CancelAsync();   // release the timer of the slot wait that lost
                    var hasItems = await waitForItem;
                    waitForItem = null;
                    if (hasItems)
                    {
                        while (queue.Reader.TryRead(out var item))
                            await ProcessAsync(item, stoppingToken);
                    }
                    continue;
                }

                if (next is null) continue;
                await RunSlotAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task ProcessAsync(SyncWorkItem item, CancellationToken ct)
    {
        var psu = item.Psu is not null && time.GetUtcNow() < item.PsuValidUntil ? item.Psu : null;
        try
        {
            if (item.Kind == SyncWorkKind.Backfill)
            {
                // Also do an incremental pass so balances exist right after consent.
                await engine.RunAsync(SyncKind.Backfill, psu, ct);
                await engine.BackfillAsync(psu, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Queued {Kind} work failed", item.Kind);
        }
    }

    private async Task RunSlotAsync(CancellationToken ct)
    {
        try
        {
            var jitter = slots.Jitter(1);
            if (jitter > TimeSpan.Zero) await Task.Delay(jitter, time, ct);
            var report = await engine.RunAsync(SyncKind.Scheduled, null, ct);
            log.LogInformation("Scheduled sync: {Accounts} account(s), {New} new, {Updated} updated, ok={Ok}", report.Accounts.Count, report.TransactionsNew, report.TransactionsUpdated, report.AllOk);
            await RunPendingBackfillAsync(null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Scheduled sync failed");
        }
    }

    private async Task RunPendingBackfillAsync(PsuContext? psu, CancellationToken ct)
    {
        if (!await engine.IsBackfillPendingAsync(ct)) return;
        var report = await engine.BackfillAsync(psu, ct);
        if (report.Accounts.Count > 0)
            log.LogInformation("Backfill pass: {New} new transaction(s); complete={Complete}", report.TransactionsNew, !await engine.IsBackfillPendingAsync(ct));
    }

    /// <summary>If the process was down at the last slot time today, run that slot once now.</summary>
    private async Task CatchUpAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lastSlot = slots.LastSlotAtOrBefore(now);
        if (lastSlot is null) return;
        if (slots.LocalDate(lastSlot.Value) != slots.LocalDate(now)) return;   // the missed slot was yesterday: leave it

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ranSince = await db.SyncRuns.AnyAsync(r => r.AccountId == null && r.Kind == SyncKind.Scheduled && r.StartedUtc >= lastSlot.Value, ct);
        if (ranSince) return;

        log.LogInformation("Catching up the {Slot} slot that was missed", slots.ToLocal(lastSlot.Value).ToString("HH:mm"));
        await RunSlotAsync(ct);
    }
}
