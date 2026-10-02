using BankSync.Api;
using BankSync.Core;
using BankSync.Data;
using Microsoft.EntityFrameworkCore;

namespace BankSync;

/// <summary>Default <see cref="IBankSync"/> implementation. Reads come from SQLite; writes to the bank never happen.</summary>
internal sealed class BankSyncService(
    IDbContextFactory<BankSyncDbContext> dbFactory,
    DatabaseInitializer initializer,
    ConsentService consent,
    SyncEngine engine,
    QuotaGuard quota,
    EnableBankingClient api) : IBankSync
{
    // ----- Consent -----------------------------------------------------------------------

    public async Task<ConsentStatus> GetConsentStatusAsync(CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        return await consent.GetStatusAsync(ct);
    }

    public async Task<Uri> BeginConsentAsync(PsuContext? psu = null, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        return await consent.BeginAsync(psu, ct);
    }

    public async Task<ConsentStatus> CompleteConsentAsync(string code, string state, PsuContext? psu = null, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        return await consent.CompleteAsync(code, state, psu, ct);
    }

    public async Task CancelConsentAsync(string state, string? error, string? description, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await consent.CancelAsync(state, error, description, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await consent.DisconnectAsync(ct);
    }

    // ----- Reads ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(bool includeDisabled = false, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.Accounts.Where(a => includeDisabled || a.Enabled).OrderBy(a => a.Id).ToListAsync(ct);
        var result = new List<AccountInfo>(accounts.Count);
        foreach (var a in accounts) result.Add(await ToInfoAsync(db, a, ct));
        return result;
    }

    public async Task<AccountInfo?> GetAccountAsync(int accountId, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.Accounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
        return a is null ? null : await ToInfoAsync(db, a, ct);
    }

    public async Task<IReadOnlyList<BalanceInfo>> GetBalancesAsync(int accountId, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var latest = await db.Balances.Where(b => b.AccountId == accountId).OrderByDescending(b => b.FetchedUtc).FirstOrDefaultAsync(ct);
        if (latest is null) return [];
        var latestFetch = latest.FetchedUtc;
        var rows = await db.Balances.Where(b => b.AccountId == accountId && b.FetchedUtc == latestFetch).ToListAsync(ct);
        return rows.Select(b => new BalanceInfo(b.AccountId, b.BalanceType, b.Name, b.Amount, b.Currency, b.ReferenceDate, b.FetchedUtc)).ToList();
    }

    public async Task<PagedResult<TransactionInfo>> GetTransactionsAsync(TransactionQuery q, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Transactions.AsNoTracking().AsQueryable();
        if (q.AccountId is { } id) query = query.Where(t => t.AccountId == id);
        if (q.From is { } from) query = query.Where(t => t.BookingDate >= from || (t.BookingDate == null && t.TransactionDate >= from));
        if (q.To is { } to) query = query.Where(t => t.BookingDate <= to || (t.BookingDate == null && t.TransactionDate <= to));
        if (q.Status is { } status) query = query.Where(t => t.Status == status);
        if (q.Category is { } cat) query = query.Where(t => t.Category == cat);
        if (q.MinAmount is { } min) query = query.Where(t => t.Amount >= min);
        if (q.MaxAmount is { } max) query = query.Where(t => t.Amount <= max);
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var needle = q.Text.Trim().ToUpperInvariant();
            query = query.Where(t =>
                (t.TextNormalized != null && t.TextNormalized.Contains(needle)) ||
                (t.CounterpartyName != null && t.CounterpartyName.ToUpper().Contains(needle)) ||
                (t.Note != null && t.Note.ToUpper().Contains(needle)));
        }

        var total = await query.CountAsync(ct);
        query = q.Descending
            ? query.OrderByDescending(t => t.BookingDate ?? t.TransactionDate).ThenByDescending(t => t.Id)
            : query.OrderBy(t => t.BookingDate ?? t.TransactionDate).ThenBy(t => t.Id);
        var rows = await query.Skip(Math.Max(0, q.Skip)).Take(Math.Clamp(q.Take, 1, 1000)).ToListAsync(ct);
        return new PagedResult<TransactionInfo>(rows.Select(ToInfo).ToList(), total, q.Skip, q.Take);
    }

    public async Task<TransactionInfo?> GetTransactionAsync(long transactionId, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == transactionId, ct);
        return t is null ? null : ToInfo(t);
    }

    public async Task<IReadOnlyList<MonthlySummary>> GetMonthlySummaryAsync(int? accountId, int months = 12, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-Math.Max(1, months) + 1);
        since = new DateOnly(since.Year, since.Month, 1);
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.Status == "BOOK" && t.BookingDate >= since && (accountId == null || t.AccountId == accountId))
            .Select(t => new { t.BookingDate, t.Amount })
            .ToListAsync(ct);
        return rows.Where(r => r.BookingDate is not null)
            .GroupBy(r => (r.BookingDate!.Value.Year, r.BookingDate!.Value.Month))
            .OrderByDescending(g => g.Key)
            .Select(g => new MonthlySummary(g.Key.Year, g.Key.Month, g.Where(r => r.Amount > 0).Sum(r => r.Amount), -g.Where(r => r.Amount < 0).Sum(r => r.Amount), g.Count()))
            .ToList();
    }

    // ----- User data -------------------------------------------------------------------------

    public async Task SetAccountDisplayNameAsync(int accountId, string? displayName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.Accounts.FirstOrDefaultAsync(x => x.Id == accountId, ct) ?? throw new BankSyncException($"Account {accountId} not found");
        a.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task SetAccountEnabledAsync(int accountId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.Accounts.FirstOrDefaultAsync(x => x.Id == accountId, ct) ?? throw new BankSyncException($"Account {accountId} not found");
        a.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateTransactionUserDataAsync(long transactionId, string? category, string? note, string[]? tags, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.Transactions.FirstOrDefaultAsync(x => x.Id == transactionId, ct) ?? throw new BankSyncException($"Transaction {transactionId} not found");
        t.Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        t.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        t.Tags = tags is { Length: > 0 } ? string.Join("\n", tags.Select(x => x.Trim()).Where(x => x.Length > 0)) : null;
        await db.SaveChangesAsync(ct);
    }

    // ----- Sync control ------------------------------------------------------------------------

    public async Task<SyncReport> RefreshNowAsync(PsuContext psu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(psu);
        await initializer.EnsureInitializedAsync(ct);
        var report = await engine.RunAsync(SyncKind.Manual, psu, ct);
        if (await engine.IsBackfillPendingAsync(ct)) await engine.BackfillAsync(psu, ct);
        return report;
    }

    public async Task<SyncReport> RunScheduledSyncAsync(CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        var report = await engine.RunAsync(SyncKind.Scheduled, null, ct);
        if (await engine.IsBackfillPendingAsync(ct)) await engine.BackfillAsync(null, ct);
        return report;
    }

    public async Task<IReadOnlyList<SyncRunInfo>> GetSyncHistoryAsync(int take = 50, CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SyncRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
        return rows.Select(r => new SyncRunInfo(r.Id, r.AccountId, r.Kind, r.Unattended, r.StartedUtc, r.FinishedUtc, r.Outcome, r.CallsBalances, r.CallsTransactions, r.TransactionsNew, r.TransactionsUpdated, r.Error)).ToList();
    }

    public async Task<QuotaStatus> GetQuotaStatusAsync(CancellationToken ct = default)
    {
        await initializer.EnsureInitializedAsync(ct);
        return await quota.GetStatusAsync(ct);
    }

    public async Task<string> VerifyCredentialsAsync(CancellationToken ct = default)
    {
        var app = await api.GetApplicationAsync(ct);
        return $"{app.Name ?? "(unnamed)"} [{app.Environment ?? "?"}] active={app.Active?.ToString() ?? "?"}";
    }

    // ----- Mapping -------------------------------------------------------------------------------

    private static async Task<AccountInfo> ToInfoAsync(BankSyncDbContext db, AccountEntity a, CancellationToken ct)
    {
        var earliest = await db.Transactions.Where(t => t.AccountId == a.Id).MinAsync(t => t.BookingDate, ct);
        var latest = await db.Transactions.Where(t => t.AccountId == a.Id).MaxAsync(t => t.BookingDate, ct);
        return new AccountInfo(a.Id, a.Iban, a.Bban, a.Name, a.DisplayName, a.Product, a.Currency, a.CashAccountType, a.Enabled, earliest, latest, a.LastSuccessfulSyncUtc, a.BackfillComplete);
    }

    private static TransactionInfo ToInfo(TransactionEntity t) => new(
        t.Id, t.AccountId, t.Status, t.BookingDate, t.ValueDate, t.TransactionDate, t.Amount, t.Currency,
        t.CounterpartyName, t.CounterpartyAccount, t.Text, t.BankTransactionCode, t.BalanceAfter, t.MerchantCategoryCode,
        t.EntryReference, t.Category, t.Note, t.Tags?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [], t.FirstSeenUtc, t.RawJson);
}
