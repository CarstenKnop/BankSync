using System.Net;
using Xunit;

namespace BankSync.Tests;

public class ConsentFlowTests
{
    [Fact]
    public async Task Fresh_install_reports_none_and_asks_for_a_connection()
    {
        using var host = new TestHost();
        var status = await host.Bank.GetConsentStatusAsync();
        Assert.Equal(ConsentState.None, status.State);
        Assert.True(status.NeedsRenewal);   // the UI shows "Connect" when this is true
        Assert.Equal(0, status.AccountCount);
    }

    [Fact]
    public async Task Begin_consent_returns_bank_url_and_requests_180_days()
    {
        using var host = new TestHost();
        var url = await host.Bank.BeginConsentAsync(host.Psu);

        Assert.Equal("https://fake.enablebanking.test/consent/abc", url.ToString());
        Assert.Equal(ConsentState.Pending, (await host.Bank.GetConsentStatusAsync()).State);
        Assert.Equal(1, host.Fake.CountCalls("POST", "auth"));
        Assert.Equal(1, host.Fake.CountCalls("GET", "aspsps"));
    }

    [Fact]
    public async Task Complete_consent_stores_accounts_and_backfills_with_psu_headers()
    {
        using var host = new TestHost();
        var today = host.Today;
        host.Fake.Transactions.AddRange(
        [
            new("er-1", "BOOK", today.AddDays(-1), -249.00m, "NETTO"),
            new("er-2", "BOOK", today.AddDays(-40), 25000.00m, "LØN", "EMPLOYER"),
            new("er-3", "BOOK", today.AddDays(-500), -1200.00m, "OLD RENT"),
        ]);

        var status = await host.ConnectAsync();

        Assert.Equal(ConsentState.Active, status.State);
        Assert.Equal(1, status.AccountCount);
        Assert.True(status.BackfillComplete);
        Assert.Equal(180, status.DaysLeft);   // the fake bank grants 180 days from the frozen test clock

        var accounts = await host.Bank.GetAccountsAsync();
        var account = Assert.Single(accounts);
        Assert.Equal("DK5000400440116243", account.Iban);
        Assert.Equal("0040-0440116243", account.Bban);
        Assert.Equal("Lønkonto", account.Name);
        Assert.Equal("••••••••••••••6243", account.MaskedIban);

        var page = await host.Bank.GetTransactionsAsync(new TransactionQuery { AccountId = account.Id });
        Assert.Equal(3, page.Total);
        Assert.Equal(-249.00m, page.Items[0].Amount);
        Assert.Equal("SHOP", page.Items[0].CounterpartyName);
        Assert.Equal("NETTO", page.Items[0].Text);
        Assert.True(page.Items[0].IsDebit);
        Assert.Equal(today.AddDays(-500), account.EarliestTransactionDate);

        var balances = await host.Bank.GetBalancesAsync(account.Id);
        Assert.Contains(balances, b => b.BalanceType == "CLBD" && b.Amount == 1000m);

        // Every data call during the consent-triggered sync carried PSU headers, so none counted against the quota.
        var dataCalls = host.Fake.Calls.Where(c => c.Path.StartsWith("accounts/")).ToList();
        Assert.NotEmpty(dataCalls);
        Assert.All(dataCalls, c => Assert.True(c.HasPsuHeaders));
        var quota = await host.Bank.GetQuotaStatusAsync();
        Assert.Equal(0, quota.Accounts.Single().TransactionsCallsUsed);
    }

    [Fact]
    public async Task Wrong_state_is_rejected_and_pending_expires()
    {
        using var host = new TestHost();
        await host.Bank.BeginConsentAsync();
        await Assert.ThrowsAsync<BankSyncConsentException>(() => host.Bank.CompleteConsentAsync("code", "not-the-state"));
        Assert.Equal(0, host.Fake.CountCalls("POST", "sessions"));

        host.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(ConsentState.None, (await host.Bank.GetConsentStatusAsync()).State);
    }

    [Fact]
    public async Task Renewal_keeps_account_identity_and_user_data()
    {
        using var host = new TestHost();
        host.Fake.Transactions.Add(new("er-1", "BOOK", host.Today.AddDays(-1), -10m, "COFFEE"));
        await host.ConnectAsync();
        var account = (await host.Bank.GetAccountsAsync()).Single();
        await host.Bank.SetAccountDisplayNameAsync(account.Id, "Daily");
        var tx = (await host.Bank.GetTransactionsAsync(new TransactionQuery())).Items.Single();
        await host.Bank.UpdateTransactionUserDataAsync(tx.Id, "Food", "with Anna", ["cafe"]);

        host.Fake.AccountUid = "acc-1-renewed";   // a new consent returns a new uid, same identification_hash
        await host.ConnectAsync();

        var accounts = await host.Bank.GetAccountsAsync();
        var same = Assert.Single(accounts);
        Assert.Equal(account.Id, same.Id);
        Assert.Equal("Daily", same.DisplayName);
        var txAfter = (await host.Bank.GetTransactionsAsync(new TransactionQuery())).Items.Single();
        Assert.Equal("Food", txAfter.Category);
        Assert.Equal("with Anna", txAfter.Note);
        Assert.Equal(["cafe"], txAfter.Tags);
        Assert.Equal(ConsentState.Active, (await host.Bank.GetConsentStatusAsync()).State);
    }

    [Fact]
    public async Task Disconnect_deletes_session_and_marks_revoked()
    {
        using var host = new TestHost();
        await host.ConnectAsync();
        await host.Bank.DisconnectAsync();
        Assert.Equal(1, host.Fake.CountCalls("DELETE", "sessions/sess-1"));
        var status = await host.Bank.GetConsentStatusAsync();
        Assert.Equal(ConsentState.Revoked, status.State);
        Assert.True(status.NeedsRenewal);
    }

    [Fact]
    public async Task Expiring_state_appears_14_days_before_valid_until()
    {
        using var host = new TestHost();
        await host.ConnectAsync();
        host.Clock.Advance(TimeSpan.FromDays(170));
        var status = await host.Bank.GetConsentStatusAsync();
        Assert.Equal(ConsentState.Expiring, status.State);
        Assert.True(status.NeedsRenewal);
        host.Clock.Advance(TimeSpan.FromDays(11));
        Assert.Equal(ConsentState.Expired, (await host.Bank.GetConsentStatusAsync()).State);
    }
}

public class QuotaTests
{
    [Fact]
    public async Task Unattended_sync_never_exceeds_four_calls_per_day_and_resets_next_day()
    {
        using var host = new TestHost();
        await host.ConnectAsync();
        host.Fake.Calls.Clear();

        for (var i = 0; i < 6; i++)
            await host.Bank.RunScheduledSyncAsync();

        Assert.Equal(4, host.Fake.CountCalls("GET", "accounts/acc-1/transactions"));
        Assert.Equal(4, host.Fake.CountCalls("GET", "accounts/acc-1/balances"));
        var history = await host.Bank.GetSyncHistoryAsync();
        Assert.Contains(history, r => r.AccountId is not null && r.Outcome == SyncOutcome.Skipped);
        Assert.Equal(4, (await host.Bank.GetQuotaStatusAsync()).Accounts.Single().TransactionsCallsUsed);

        host.Clock.Advance(TimeSpan.FromDays(1));
        await host.Bank.RunScheduledSyncAsync();
        Assert.Equal(5, host.Fake.CountCalls("GET", "accounts/acc-1/transactions"));
        Assert.Equal(1, (await host.Bank.GetQuotaStatusAsync()).Accounts.Single().TransactionsCallsUsed);
    }

    [Fact]
    public async Task Manual_refresh_sends_psu_headers_and_is_not_counted()
    {
        using var host = new TestHost();
        await host.ConnectAsync();
        host.Fake.Calls.Clear();

        var report = await host.Bank.RefreshNowAsync(host.Psu);
        Assert.True(report.AllOk);
        Assert.All(host.Fake.Calls.Where(c => c.Path.StartsWith("accounts/")), c => Assert.True(c.HasPsuHeaders));
        Assert.Equal(0, (await host.Bank.GetQuotaStatusAsync()).Accounts.Single().TransactionsCallsUsed);

        // debounced
        host.Fake.Calls.Clear();
        var second = await host.Bank.RefreshNowAsync(host.Psu);
        Assert.Empty(second.Accounts);
        Assert.Empty(host.Fake.Calls);
    }

    [Fact]
    public async Task Rate_limit_is_recorded_without_retry()
    {
        using var host = new TestHost();
        await host.ConnectAsync();
        host.Fake.Calls.Clear();
        host.Fake.Override = r => r.RequestUri!.AbsolutePath.EndsWith("/balances") ? FakeEnableBanking.Json(HttpStatusCode.TooManyRequests, """{"error":"TOO_MANY_REQUESTS","message":"quota"}""") : null;

        var report = await host.Bank.RunScheduledSyncAsync();

        Assert.Equal(SyncOutcome.RateLimited, report.Accounts.Single().Outcome);
        Assert.Equal(1, host.Fake.CountCalls("GET", "accounts/acc-1/balances"));
        Assert.True((await host.Bank.GetQuotaStatusAsync()).Accounts.Single().RateLimitedToday);
        Assert.Equal(ConsentState.Active, (await host.Bank.GetConsentStatusAsync()).State);
    }

    [Fact]
    public async Task Backfill_pauses_on_quota_and_resumes_next_day()
    {
        using var host = new TestHost(o => o.BackfillMaxYears = 10, now: new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.FromHours(2)));
        for (var y = 0; y < 8; y++)
            host.Fake.Transactions.Add(new($"er-{y}", "BOOK", host.Today.AddYears(-y).AddDays(-10), -100m - y, $"YEAR {y}"));

        await host.ConnectAsync(withPsu: false);   // unattended: 4 windows today

        var status = await host.Bank.GetConsentStatusAsync();
        Assert.False(status.BackfillComplete);
        Assert.Equal(4, host.Fake.CountCalls("GET", "accounts/acc-1/transactions"));

        host.Clock.Advance(TimeSpan.FromDays(1));
        await host.Bank.RunScheduledSyncAsync();   // 1 incremental + 3 backfill windows
        host.Clock.Advance(TimeSpan.FromDays(1));
        await host.Bank.RunScheduledSyncAsync();
        host.Clock.Advance(TimeSpan.FromDays(1));
        await host.Bank.RunScheduledSyncAsync();

        var accounts = await host.Bank.GetAccountsAsync();
        Assert.Equal(host.Today.AddDays(-3).AddYears(-7).AddDays(-10), accounts.Single().EarliestTransactionDate);
        Assert.Equal(8, (await host.Bank.GetTransactionsAsync(new TransactionQuery { Take = 100 })).Total);
    }
}

public class DedupTests
{
    [Fact]
    public async Task Same_data_twice_adds_nothing()
    {
        using var host = new TestHost();
        host.Fake.Transactions.Add(new("er-1", "BOOK", host.Today.AddDays(-1), -10m, "A"));
        host.Fake.Transactions.Add(new(null, "BOOK", host.Today.AddDays(-2), -20m, "B"));   // content-hash keyed
        await host.ConnectAsync();

        var report = await host.Bank.RefreshNowAsync(host.Psu);
        Assert.Equal(0, report.TransactionsNew);
        Assert.Equal(0, report.TransactionsUpdated);
        Assert.Equal(2, (await host.Bank.GetTransactionsAsync(new TransactionQuery())).Total);
    }

    [Fact]
    public async Task Pending_becomes_booked_in_place()
    {
        using var host = new TestHost();
        host.Fake.Transactions.Add(new(null, "PEND", host.Today, -55m, "CAFE X"));
        await host.ConnectAsync();
        var pending = (await host.Bank.GetTransactionsAsync(new TransactionQuery())).Items.Single();
        Assert.True(pending.IsPending);
        await host.Bank.UpdateTransactionUserDataAsync(pending.Id, "Food", null, null);

        host.Fake.Transactions.Clear();
        host.Fake.Transactions.Add(new("er-booked", "BOOK", host.Today.AddDays(1), -55m, "CAFE X"));
        host.Clock.Advance(TimeSpan.FromDays(1));
        var report = await host.Bank.RefreshNowAsync(host.Psu);

        Assert.Equal(0, report.TransactionsNew);
        Assert.Equal(1, report.TransactionsUpdated);
        var booked = (await host.Bank.GetTransactionsAsync(new TransactionQuery())).Items.Single();
        Assert.Equal(pending.Id, booked.Id);
        Assert.False(booked.IsPending);
        Assert.Equal("er-booked", booked.EntryReference);
        Assert.Equal("Food", booked.Category);
    }

    [Fact]
    public async Task Pagination_follows_continuation_key()
    {
        using var host = new TestHost();
        host.Fake.PageSize = 2;
        for (var i = 0; i < 5; i++) host.Fake.Transactions.Add(new($"er-{i}", "BOOK", host.Today.AddDays(-i), -1m * (i + 1), $"T{i}"));
        await host.ConnectAsync();
        Assert.Equal(5, (await host.Bank.GetTransactionsAsync(new TransactionQuery())).Total);
        Assert.Contains(host.Fake.Calls, c => c.Query?.Contains("continuation_key=") == true);
    }
}

public class ExpiryTests
{
    [Fact]
    public async Task Expired_session_error_marks_consent_expired_and_stops()
    {
        using var host = new TestHost();
        await host.ConnectAsync();
        host.Fake.Override = r => r.RequestUri!.AbsolutePath.Contains("/accounts/") ? FakeEnableBanking.Json(HttpStatusCode.UnprocessableEntity, """{"error":"EXPIRED_SESSION","message":"session expired"}""") : null;

        var report = await host.Bank.RunScheduledSyncAsync();

        Assert.Equal(SyncOutcome.ConsentExpired, report.Accounts.Single().Outcome);
        var status = await host.Bank.GetConsentStatusAsync();
        Assert.Equal(ConsentState.Expired, status.State);
        Assert.True(status.NeedsRenewal);

        host.Fake.Calls.Clear();
        var again = await host.Bank.RunScheduledSyncAsync();   // nothing to do without an active consent
        Assert.Empty(again.Accounts);
        Assert.Empty(host.Fake.Calls);
    }

    [Fact]
    public async Task Listener_is_notified()
    {
        var listener = new RecordingListener();
        using var host = new TestHost(configureServices: s => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IBankSyncListener>(s, listener));
        host.Fake.Transactions.Add(new("er-1", "BOOK", host.Today, -1m, "X"));
        await host.ConnectAsync();
        Assert.Contains(ConsentState.Active, listener.States);
        Assert.True(listener.TransactionsChanged > 0);
        Assert.True(listener.SyncCompleted > 0);
    }

    private sealed class RecordingListener : IBankSyncListener
    {
        public List<ConsentState> States { get; } = [];
        public int TransactionsChanged, SyncCompleted;
        public Task OnConsentStateChangedAsync(ConsentState state, CancellationToken ct) { States.Add(state); return Task.CompletedTask; }
        public Task OnSyncCompletedAsync(SyncReport report, CancellationToken ct) { SyncCompleted++; return Task.CompletedTask; }
        public Task OnTransactionsChangedAsync(int accountId, int added, int updated, CancellationToken ct) { TransactionsChanged++; return Task.CompletedTask; }
    }

}
