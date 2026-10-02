using BankSync.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BankSync.Core;

/// <summary>
/// Bookkeeping for the PSD2 "four unattended calls per account per day" rule. Every data call is recorded;
/// scheduled work asks before calling. Calls with PSU headers are recorded but not limited.
/// </summary>
internal sealed class QuotaGuard(
    IDbContextFactory<BankSyncDbContext> dbFactory,
    IOptions<BankSyncOptions> options,
    SlotCalculator slots,
    TimeProvider time)
{
    public int Limit => options.Value.MaxUnattendedCallsPerDay;

    public async Task<int> UsedTodayAsync(int accountId, CallKind kind, CancellationToken ct)
    {
        var day = slots.LocalDay(time.GetUtcNow());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiCalls.CountAsync(c => c.AccountId == accountId && c.LocalDay == day && c.Kind == kind && c.Unattended, ct);
    }

    public async Task<bool> CanCallUnattendedAsync(int accountId, CallKind kind, CancellationToken ct)
        => await UsedTodayAsync(accountId, kind, ct) < Limit;

    public async Task<bool> WasRateLimitedTodayAsync(int accountId, CancellationToken ct)
    {
        var day = slots.LocalDay(time.GetUtcNow());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiCalls.AnyAsync(c => c.AccountId == accountId && c.LocalDay == day && c.StatusCode == 429, ct);
    }

    public async Task RecordAsync(int accountId, CallKind kind, bool unattended, int statusCode, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiCalls.Add(new ApiCallEntity
        {
            AccountId = accountId,
            LocalDay = slots.LocalDay(now),
            Kind = kind,
            Unattended = unattended,
            CalledUtc = now,
            StatusCode = statusCode
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<QuotaStatus> GetStatusAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var day = slots.LocalDay(now);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var accountIds = await db.Accounts.Where(a => a.Enabled).Select(a => a.Id).ToListAsync(ct);
        var calls = await db.ApiCalls.Where(c => c.LocalDay == day).ToListAsync(ct);
        var list = accountIds.Select(id => new AccountQuota(
            id,
            calls.Count(c => c.AccountId == id && c.Unattended && c.Kind == CallKind.Balances),
            calls.Count(c => c.AccountId == id && c.Unattended && c.Kind == CallKind.Transactions),
            calls.Any(c => c.AccountId == id && c.StatusCode == 429))).ToList();
        return new QuotaStatus(slots.LocalDate(now), Limit, list);
    }
}
