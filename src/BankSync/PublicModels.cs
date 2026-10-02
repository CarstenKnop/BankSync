namespace BankSync;

/// <summary>State of the current consent (Enable Banking session).</summary>
public enum ConsentState
{
    /// <summary>No consent has ever been given, or the last one was replaced.</summary>
    None,
    /// <summary>BeginConsent was called; waiting for the callback.</summary>
    Pending,
    /// <summary>Data can be fetched.</summary>
    Active,
    /// <summary>Still active but within <see cref="BankSyncOptions.RenewalWarningDays"/> of expiry.</summary>
    Expiring,
    /// <summary>The bank or Enable Banking ended the session, or valid_until passed.</summary>
    Expired,
    /// <summary>The owner disconnected.</summary>
    Revoked
}

public enum SyncKind { Scheduled, Backfill, Manual }
public enum SyncOutcome { Ok, Skipped, RateLimited, ConsentExpired, Error }

/// <summary>Identifies the end user as "present" for an API call (PSD2). Build it from the owner's own HTTP request, never from a background job.</summary>
public sealed record PsuContext(string IpAddress, string UserAgent, string? AcceptLanguage = null);

public sealed record ConsentStatus(
    ConsentState State,
    DateTimeOffset? ValidUntil,
    int? DaysLeft,
    bool NeedsRenewal,
    DateTimeOffset? CreatedAt,
    int AccountCount,
    DateTimeOffset? LastSuccessfulSync,
    bool BackfillComplete,
    string? LastError);

public sealed record AccountInfo(
    int Id,
    string? Iban,
    string? Bban,
    string? Name,
    string? DisplayName,
    string? Product,
    string? Currency,
    string? CashAccountType,
    bool Enabled,
    DateOnly? EarliestTransactionDate,
    DateOnly? LatestTransactionDate,
    DateTimeOffset? LastSyncedAt,
    bool BackfillComplete)
{
    public string MaskedIban => Iban is { Length: > 4 } ? new string('•', Math.Max(0, Iban.Length - 4)) + Iban[^4..] : Iban ?? "";
}

public sealed record BalanceInfo(
    int AccountId,
    string BalanceType,
    string? Name,
    decimal Amount,
    string Currency,
    DateOnly? ReferenceDate,
    DateTimeOffset FetchedAt);

public sealed record TransactionInfo(
    long Id,
    int AccountId,
    string Status,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    DateOnly? TransactionDate,
    decimal Amount,
    string Currency,
    string? CounterpartyName,
    string? CounterpartyAccount,
    string? Text,
    string? BankTransactionCode,
    decimal? BalanceAfter,
    string? MerchantCategoryCode,
    string? EntryReference,
    string? Category,
    string? Note,
    string[] Tags,
    DateTimeOffset FirstSeenAt,
    string RawJson)
{
    public bool IsPending => Status == "PEND";
    public bool IsDebit => Amount < 0;
}

public sealed record TransactionQuery
{
    public int? AccountId { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    /// <summary>Case-insensitive substring match on counterparty, text and note.</summary>
    public string? Text { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    /// <summary>"BOOK", "PEND" or null for both.</summary>
    public string? Status { get; init; }
    public string? Category { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 50;
    /// <summary>Newest first when true (default).</summary>
    public bool Descending { get; init; } = true;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

public sealed record MonthlySummary(int Year, int Month, decimal Income, decimal Spend, int Count);

public sealed record SyncReport(
    SyncKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<AccountSyncResult> Accounts)
{
    public int TransactionsNew => Accounts.Sum(a => a.TransactionsNew);
    public int TransactionsUpdated => Accounts.Sum(a => a.TransactionsUpdated);
    public bool AllOk => Accounts.All(a => a.Outcome is SyncOutcome.Ok or SyncOutcome.Skipped);
}

public sealed record AccountSyncResult(
    int AccountId,
    SyncOutcome Outcome,
    int CallsBalances,
    int CallsTransactions,
    int TransactionsNew,
    int TransactionsUpdated,
    string? Error);

public sealed record SyncRunInfo(
    long Id,
    int? AccountId,
    SyncKind Kind,
    bool Unattended,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    SyncOutcome Outcome,
    int CallsBalances,
    int CallsTransactions,
    int TransactionsNew,
    int TransactionsUpdated,
    string? Error);

public sealed record QuotaStatus(DateOnly LocalDay, int Limit, IReadOnlyList<AccountQuota> Accounts);
public sealed record AccountQuota(int AccountId, int BalancesCallsUsed, int TransactionsCallsUsed, bool RateLimitedToday);

/// <summary>Optional hook for the host app. Register an implementation to be notified; all methods are called from background threads.</summary>
public interface IBankSyncListener
{
    Task OnConsentStateChangedAsync(ConsentState state, CancellationToken ct) => Task.CompletedTask;
    Task OnSyncCompletedAsync(SyncReport report, CancellationToken ct) => Task.CompletedTask;
    Task OnTransactionsChangedAsync(int accountId, int added, int updated, CancellationToken ct) => Task.CompletedTask;
}
