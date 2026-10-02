using BankSync.Api;
using BankSync.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankSync.Core;

/// <summary>
/// Fetches balances and transactions for every account of the active consent, within the quota, and
/// upserts them. Runs are serialised so scheduled, manual and backfill work never overlap.
/// </summary>
internal sealed class SyncEngine(
    IDbContextFactory<BankSyncDbContext> dbFactory,
    EnableBankingClient api,
    QuotaGuard quota,
    SecretProtector protector,
    SlotCalculator slots,
    IOptions<BankSyncOptions> options,
    TimeProvider time,
    IEnumerable<IBankSyncListener> listeners,
    ILogger<SyncEngine> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastManualRun = DateTimeOffset.MinValue;

    /// <summary>Incremental sync of all enabled accounts. Unattended when <paramref name="psu"/> is null.</summary>
    public async Task<SyncReport> RunAsync(SyncKind kind, PsuContext? psu, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var started = time.GetUtcNow();
            if (kind == SyncKind.Manual)
            {
                if (started - _lastManualRun < options.Value.ManualRefreshCooldown)
                    return new SyncReport(kind, started, started, []);
                _lastManualRun = started;
            }

            var results = new List<AccountSyncResult>();
            var ctx = await LoadActiveConsentAsync(ct);
            if (ctx is null)
            {
                await RecordSummaryAsync(kind, psu is null, started, results, ct);
                return new SyncReport(kind, started, time.GetUtcNow(), results);
            }

            foreach (var (account, uid) in ctx.Value.Accounts)
            {
                ct.ThrowIfCancellationRequested();
                var result = await SyncAccountAsync(ctx.Value.ConsentId, account, uid, kind, psu, ct);
                results.Add(result);
                if (result.Outcome == SyncOutcome.ConsentExpired) break;
            }

            await RecordSummaryAsync(kind, psu is null, started, results, ct);
            var report = new SyncReport(kind, started, time.GetUtcNow(), results);
            await NotifyAsync(l => l.OnSyncCompletedAsync(report, ct));
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Walks backwards in windows until the history is exhausted or the quota is used up. Resumable.</summary>
    public async Task<SyncReport> BackfillAsync(PsuContext? psu, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var started = time.GetUtcNow();
            var results = new List<AccountSyncResult>();
            var ctx = await LoadActiveConsentAsync(ct);
            if (ctx is null) return new SyncReport(SyncKind.Backfill, started, started, results);

            foreach (var (account, uid) in ctx.Value.Accounts)
            {
                ct.ThrowIfCancellationRequested();
                if (account.BackfillComplete) continue;
                var result = await BackfillAccountAsync(ctx.Value.ConsentId, account, uid, psu, ct);
                results.Add(result);
                if (result.Outcome == SyncOutcome.ConsentExpired) break;
            }

            await RecordSummaryAsync(SyncKind.Backfill, psu is null, started, results, ct);
            var report = new SyncReport(SyncKind.Backfill, started, time.GetUtcNow(), results);
            if (results.Count > 0) await NotifyAsync(l => l.OnSyncCompletedAsync(report, ct));
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsBackfillPendingAsync(CancellationToken ct)
    {
        var ctx = await LoadActiveConsentAsync(ct);
        return ctx is not null && ctx.Value.Accounts.Any(a => !a.Account.BackfillComplete);
    }

    // ---------------------------------------------------------------------------------

    private async Task<AccountSyncResult> SyncAccountAsync(int consentId, AccountEntity account, string uid, SyncKind kind, PsuContext? psu, CancellationToken ct)
    {
        var unattended = psu is null;
        var run = new SyncRunEntity { AccountId = account.Id, Kind = kind, Unattended = unattended, StartedUtc = time.GetUtcNow() };
        int added = 0, updated = 0;
        try
        {
            // Balances
            if (!unattended || await quota.CanCallUnattendedAsync(account.Id, CallKind.Balances, ct))
            {
                var balances = await CallAsync(account.Id, CallKind.Balances, unattended, () => api.GetBalancesAsync(uid, psu, ct), ct);
                run.CallsBalances++;
                await StoreBalancesAsync(account.Id, balances, ct);
            }
            else
            {
                log.LogInformation("Account {AccountId}: balances quota used up for today, skipping", account.Id);
            }

            // Transactions, incremental window
            if (!unattended || await quota.CanCallUnattendedAsync(account.Id, CallKind.Transactions, ct))
            {
                var today = slots.LocalDate(time.GetUtcNow());
                var floor = today.AddDays(-89);
                var from = (account.LastBookedDate ?? floor).AddDays(-options.Value.OverlapDays);
                if (from < floor) from = floor;
                var (a, u, calls) = await FetchAndUpsertWindowAsync(account.Id, uid, from, today, unattended, psu, ct);
                added += a; updated += u; run.CallsTransactions += calls;
            }
            else
            {
                log.LogInformation("Account {AccountId}: transactions quota used up for today, skipping", account.Id);
                if (run.CallsBalances == 0) run.Outcome = SyncOutcome.Skipped;
            }

            if (run.Outcome != SyncOutcome.Skipped) run.Outcome = SyncOutcome.Ok;
            await UpdateAccountAfterSyncAsync(account.Id, success: run.Outcome == SyncOutcome.Ok, error: null, ct);
        }
        catch (EnableBankingException ex)
        {
            run.Outcome = await ClassifyAsync(consentId, account.Id, ex, ct);
            run.Error = Truncate(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Account {AccountId}: sync failed", account.Id);
            run.Outcome = SyncOutcome.Error;
            run.Error = Truncate(ex.Message);
            await UpdateAccountAfterSyncAsync(account.Id, success: false, error: run.Error, ct);
        }

        run.TransactionsNew = added;
        run.TransactionsUpdated = updated;
        run.FinishedUtc = time.GetUtcNow();
        await SaveRunAsync(run, ct);
        if (added + updated > 0) await NotifyAsync(l => l.OnTransactionsChangedAsync(account.Id, added, updated, ct));
        return new AccountSyncResult(account.Id, run.Outcome, run.CallsBalances, run.CallsTransactions, added, updated, run.Error);
    }

    private async Task<AccountSyncResult> BackfillAccountAsync(int consentId, AccountEntity account, string uid, PsuContext? psu, CancellationToken ct)
    {
        var unattended = psu is null;
        var o = options.Value;
        var run = new SyncRunEntity { AccountId = account.Id, Kind = SyncKind.Backfill, Unattended = unattended, StartedUtc = time.GetUtcNow() };
        int added = 0, updated = 0;
        var today = slots.LocalDate(time.GetUtcNow());
        var oldest = today.AddYears(-o.BackfillMaxYears);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var reached = account.BackfillReachedDate;
                var to = reached is null ? today : reached.Value.AddDays(-1);
                var from = to.AddDays(-(o.BackfillWindowDays - 1));
                if (to < oldest) { await MarkBackfillCompleteAsync(account.Id, ct); break; }
                if (from < oldest) from = oldest;

                if (unattended && !await quota.CanCallUnattendedAsync(account.Id, CallKind.Transactions, ct))
                {
                    log.LogInformation("Account {AccountId}: backfill paused, quota used up for today (reached {Date})", account.Id, reached);
                    run.Outcome = SyncOutcome.Skipped;
                    break;
                }

                int count;
                try
                {
                    var (a, u, calls, fetched) = await FetchAndUpsertWindowCountingAsync(account.Id, uid, from, to, unattended, psu, ct);
                    added += a; updated += u; run.CallsTransactions += calls; count = fetched;
                }
                catch (EnableBankingException ex) when (!ex.IsRateLimited && !ex.IsSessionExpired && !ex.IsAuthProblem && !ex.IsTransient && (int)ex.Status is >= 400 and < 500)
                {
                    // The bank refuses the date range: history ends here.
                    log.LogInformation("Account {AccountId}: bank rejected window {From}..{To} ({Code}); treating as end of history", account.Id, from, to, ex.Code);
                    await MarkBackfillCompleteAsync(account.Id, ct);
                    break;
                }

                account.BackfillReachedDate = from;
                await SetBackfillReachedAsync(account.Id, from, ct);

                var twoYearsBack = today.AddYears(-2);
                if (count == 0 && from <= twoYearsBack)
                {
                    await MarkBackfillCompleteAsync(account.Id, ct);
                    break;
                }
            }
            if (run.Outcome != SyncOutcome.Skipped) run.Outcome = SyncOutcome.Ok;
        }
        catch (EnableBankingException ex)
        {
            run.Outcome = await ClassifyAsync(consentId, account.Id, ex, ct);
            run.Error = Truncate(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Account {AccountId}: backfill failed", account.Id);
            run.Outcome = SyncOutcome.Error;
            run.Error = Truncate(ex.Message);
        }

        run.TransactionsNew = added;
        run.TransactionsUpdated = updated;
        run.FinishedUtc = time.GetUtcNow();
        await SaveRunAsync(run, ct);
        if (added + updated > 0) await NotifyAsync(l => l.OnTransactionsChangedAsync(account.Id, added, updated, ct));
        return new AccountSyncResult(account.Id, run.Outcome, 0, run.CallsTransactions, added, updated, run.Error);
    }

    private async Task<(int added, int updated, int calls)> FetchAndUpsertWindowAsync(int accountId, string uid, DateOnly from, DateOnly to, bool unattended, PsuContext? psu, CancellationToken ct)
    {
        var (a, u, c, _) = await FetchAndUpsertWindowCountingAsync(accountId, uid, from, to, unattended, psu, ct);
        return (a, u, c);
    }

    private async Task<(int added, int updated, int calls, int fetched)> FetchAndUpsertWindowCountingAsync(int accountId, string uid, DateOnly from, DateOnly to, bool unattended, PsuContext? psu, CancellationToken ct)
    {
        int added = 0, updated = 0, calls = 0, fetched = 0;
        string? key = null;
        do
        {
            if (unattended && calls > 0 && !await quota.CanCallUnattendedAsync(accountId, CallKind.Transactions, ct))
            {
                log.LogWarning("Account {AccountId}: pagination stopped by quota; remaining pages will be fetched in a later run", accountId);
                break;
            }
            var currentKey = key;
            var page = await CallAsync(accountId, CallKind.Transactions, unattended, () => api.GetTransactionsPageAsync(uid, from, to, currentKey, psu, ct), ct);
            calls++;
            var items = page.Transactions ?? [];
            fetched += items.Length;
            var (a, u) = await UpsertAsync(accountId, items, ct);
            added += a; updated += u;
            key = page.ContinuationKey;
        } while (!string.IsNullOrEmpty(key));
        return (added, updated, calls, fetched);
    }

    /// <summary>Idempotent upsert with the dedup rules from the specification. Never touches user fields.</summary>
    internal async Task<(int added, int updated)> UpsertAsync(int accountId, IEnumerable<Transaction> items, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        int added = 0, updated = 0;
        DateOnly? maxBooked = null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var raw in items)
        {
            var incoming = TransactionMapper.Map(raw, accountId, now);
            if (incoming.BookingDate is { } bd && (maxBooked is null || bd > maxBooked)) maxBooked = bd;

            var existing = await db.Transactions.FirstOrDefaultAsync(t => t.AccountId == accountId && t.DedupHash == incoming.DedupHash, ct);

            if (existing is null && incoming.Status == "BOOK")
            {
                // A pending record for the same movement may exist under a content hash; take it over.
                var fromDate = (incoming.BookingDate ?? incoming.TransactionDate ?? DateOnly.MinValue).AddDays(-7);
                var toDate = (incoming.BookingDate ?? incoming.TransactionDate ?? DateOnly.MaxValue).AddDays(7);
                var pendingCandidates = await db.Transactions
                    .Where(t => t.AccountId == accountId && t.Status == "PEND" && t.Amount == incoming.Amount && t.Currency == incoming.Currency)
                    .ToListAsync(ct);
                existing = pendingCandidates.FirstOrDefault(t =>
                    t.TextNormalized == incoming.TextNormalized &&
                    (t.BookingDate ?? t.TransactionDate ?? DateOnly.MinValue) >= fromDate &&
                    (t.BookingDate ?? t.TransactionDate ?? DateOnly.MinValue) <= toDate);
            }

            if (existing is null)
            {
                db.Transactions.Add(incoming);
                added++;
                continue;
            }

            var changed = existing.Status != incoming.Status
                          || existing.BookingDate != incoming.BookingDate
                          || existing.ValueDate != incoming.ValueDate
                          || existing.BalanceAfter != incoming.BalanceAfter
                          || existing.DedupHash != incoming.DedupHash;
            existing.DedupHash = incoming.DedupHash;
            existing.EntryReference = incoming.EntryReference ?? existing.EntryReference;
            existing.TransactionId = incoming.TransactionId ?? existing.TransactionId;
            existing.Status = incoming.Status;
            existing.BookingDate = incoming.BookingDate ?? existing.BookingDate;
            existing.ValueDate = incoming.ValueDate ?? existing.ValueDate;
            existing.TransactionDate = incoming.TransactionDate ?? existing.TransactionDate;
            existing.BalanceAfter = incoming.BalanceAfter ?? existing.BalanceAfter;
            existing.CounterpartyName = incoming.CounterpartyName ?? existing.CounterpartyName;
            existing.CounterpartyAccount = incoming.CounterpartyAccount ?? existing.CounterpartyAccount;
            existing.Text = incoming.Text ?? existing.Text;
            existing.TextNormalized = incoming.TextNormalized ?? existing.TextNormalized;
            existing.BankTransactionCode = incoming.BankTransactionCode ?? existing.BankTransactionCode;
            existing.MerchantCategoryCode = incoming.MerchantCategoryCode ?? existing.MerchantCategoryCode;
            existing.RawJson = incoming.RawJson;
            existing.LastSeenUtc = now;
            if (changed) updated++;
        }

        if (maxBooked is not null)
        {
            var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
            if (account.LastBookedDate is null || maxBooked > account.LastBookedDate) account.LastBookedDate = maxBooked;
        }
        await db.SaveChangesAsync(ct);
        return (added, updated);
    }

    private async Task StoreBalancesAsync(int accountId, HalBalances balances, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var b in balances.Balances ?? [])
        {
            db.Balances.Add(new BalanceSnapshotEntity
            {
                AccountId = accountId,
                FetchedUtc = now,
                BalanceType = b.BalanceType ?? "OTHR",
                Name = b.Name,
                Amount = TransactionMapper.ParseAmount(b.BalanceAmount?.Value),
                Currency = b.BalanceAmount?.Currency ?? "",
                ReferenceDate = TransactionMapper.ParseDate(b.ReferenceDate)
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<T> CallAsync<T>(int accountId, CallKind kind, bool unattended, Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            var result = await call();
            await quota.RecordAsync(accountId, kind, unattended, 200, ct);
            return result;
        }
        catch (EnableBankingException ex)
        {
            await quota.RecordAsync(accountId, kind, unattended, (int)ex.Status, ct);
            throw;
        }
    }

    private async Task<SyncOutcome> ClassifyAsync(int consentId, int accountId, EnableBankingException ex, CancellationToken ct)
    {
        if (ex.IsSessionExpired)
        {
            log.LogWarning("Consent {ConsentId} reported expired by Enable Banking: {Code}", consentId, ex.Code);
            await MarkConsentExpiredAsync(consentId, ex.Code, ct);
            await UpdateAccountAfterSyncAsync(accountId, false, "Consent expired", ct);
            return SyncOutcome.ConsentExpired;
        }
        if (ex.IsRateLimited)
        {
            log.LogWarning("Account {AccountId}: rate limited (429); next attempt at the next slot", accountId);
            await UpdateAccountAfterSyncAsync(accountId, false, "Rate limited", ct);
            return SyncOutcome.RateLimited;
        }
        log.LogError("Account {AccountId}: Enable Banking error {Status} {Code}", accountId, (int)ex.Status, ex.Code);
        await UpdateAccountAfterSyncAsync(accountId, false, Truncate(ex.Message), ct);
        return SyncOutcome.Error;
    }

    internal async Task MarkConsentExpiredAsync(int consentId, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var consent = await db.Consents.FirstOrDefaultAsync(c => c.Id == consentId, ct);
        if (consent is null || consent.Status is ConsentState.Expired or ConsentState.Revoked) return;
        consent.Status = ConsentState.Expired;
        consent.EndedUtc = time.GetUtcNow();
        consent.EndReason = reason;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(l => l.OnConsentStateChangedAsync(ConsentState.Expired, ct));
    }

    private async Task UpdateAccountAfterSyncAsync(int accountId, bool success, string? error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        if (success) account.LastSuccessfulSyncUtc = time.GetUtcNow();
        account.LastError = error;
        await db.SaveChangesAsync(ct);
    }

    private async Task SetBackfillReachedAsync(int accountId, DateOnly reached, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        account.BackfillReachedDate = reached;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkBackfillCompleteAsync(int accountId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        account.BackfillComplete = true;
        account.BackfillCompletedUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        log.LogInformation("Account {AccountId}: backfill complete back to {Date}", accountId, account.BackfillReachedDate);
    }

    private async Task SaveRunAsync(SyncRunEntity run, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordSummaryAsync(SyncKind kind, bool unattended, DateTimeOffset started, List<AccountSyncResult> results, CancellationToken ct)
    {
        var outcome = results.Count == 0 ? SyncOutcome.Skipped
            : results.Any(r => r.Outcome == SyncOutcome.ConsentExpired) ? SyncOutcome.ConsentExpired
            : results.Any(r => r.Outcome == SyncOutcome.Error) ? SyncOutcome.Error
            : results.Any(r => r.Outcome == SyncOutcome.RateLimited) ? SyncOutcome.RateLimited
            : results.All(r => r.Outcome == SyncOutcome.Skipped) ? SyncOutcome.Skipped
            : SyncOutcome.Ok;
        await SaveRunAsync(new SyncRunEntity
        {
            AccountId = null,
            Kind = kind,
            Unattended = unattended,
            StartedUtc = started,
            FinishedUtc = time.GetUtcNow(),
            Outcome = outcome,
            CallsBalances = results.Sum(r => r.CallsBalances),
            CallsTransactions = results.Sum(r => r.CallsTransactions),
            TransactionsNew = results.Sum(r => r.TransactionsNew),
            TransactionsUpdated = results.Sum(r => r.TransactionsUpdated),
            Error = results.FirstOrDefault(r => r.Error is not null)?.Error
        }, ct);
    }

    private async Task<(int ConsentId, List<(AccountEntity Account, string Uid)> Accounts)?> LoadActiveConsentAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var consent = await db.Consents.Include(c => c.Accounts).ThenInclude(ca => ca.Account)
            .OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        if (consent is null || consent.Status is not (ConsentState.Active or ConsentState.Expiring)) return null;
        if (consent.ValidUntilUtc <= time.GetUtcNow())
        {
            await MarkConsentExpiredAsync(consent.Id, "valid_until passed", ct);
            return null;
        }
        _ = protector; // session id is not needed for data calls; Enable Banking resolves accounts by uid
        var accounts = consent.Accounts.Where(ca => ca.Account.Enabled).Select(ca => (ca.Account, ca.Uid)).ToList();
        return (consent.Id, accounts);
    }

    private async Task NotifyAsync(Func<IBankSyncListener, Task> action)
    {
        foreach (var l in listeners)
        {
            try { await action(l); }
            catch (Exception ex) { log.LogWarning(ex, "IBankSyncListener {Listener} threw", l.GetType().Name); }
        }
    }

    private static string? Truncate(string? s) => s is null ? null : s.Length <= 500 ? s : s[..500];
}
