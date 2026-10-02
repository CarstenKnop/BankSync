namespace BankSync;

/// <summary>
/// The only interface a host app needs. All read methods serve from the local database and never call
/// Enable Banking; the library keeps the database current in the background within the PSD2 quota.
/// </summary>
public interface IBankSync
{
    // ----- Consent -------------------------------------------------------------------

    /// <summary>Current consent state, expiry and sync health. Cheap; call it on every page.</summary>
    Task<ConsentStatus> GetConsentStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Starts a new consent. Returns the URL the owner must open (redirect the browser to it).
    /// After MitID approval the browser lands on <see cref="BankSyncOptions.RedirectUrl"/>?code=…&amp;state=…;
    /// pass those two values to <see cref="CompleteConsentAsync"/>.
    /// </summary>
    Task<Uri> BeginConsentAsync(PsuContext? psu = null, CancellationToken ct = default);

    /// <summary>
    /// Finishes the consent from the callback query parameters, stores the session and accounts, and starts the
    /// full-history backfill in the background. Pass the owner's <paramref name="psu"/> so the first hour of backfill
    /// is quota-exempt. Throws <see cref="BankSyncConsentException"/> on a bad or expired state.
    /// </summary>
    Task<ConsentStatus> CompleteConsentAsync(string code, string state, PsuContext? psu = null, CancellationToken ct = default);

    /// <summary>Call this when the callback arrives with error/error_description instead of a code.</summary>
    Task CancelConsentAsync(string state, string? error, string? description, CancellationToken ct = default);

    /// <summary>Revokes the consent at Enable Banking and the bank. Local data is kept.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    // ----- Reads (local database only) ------------------------------------------------

    Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(bool includeDisabled = false, CancellationToken ct = default);
    Task<AccountInfo?> GetAccountAsync(int accountId, CancellationToken ct = default);

    /// <summary>Latest snapshot of each balance type for the account.</summary>
    Task<IReadOnlyList<BalanceInfo>> GetBalancesAsync(int accountId, CancellationToken ct = default);

    Task<PagedResult<TransactionInfo>> GetTransactionsAsync(TransactionQuery query, CancellationToken ct = default);
    Task<TransactionInfo?> GetTransactionAsync(long transactionId, CancellationToken ct = default);
    Task<IReadOnlyList<MonthlySummary>> GetMonthlySummaryAsync(int? accountId, int months = 12, CancellationToken ct = default);

    // ----- User data on transactions ---------------------------------------------------

    Task SetAccountDisplayNameAsync(int accountId, string? displayName, CancellationToken ct = default);
    Task SetAccountEnabledAsync(int accountId, bool enabled, CancellationToken ct = default);
    Task UpdateTransactionUserDataAsync(long transactionId, string? category, string? note, string[]? tags, CancellationToken ct = default);

    // ----- Sync control and diagnostics --------------------------------------------------

    /// <summary>
    /// Fetches now, with the owner's PSU headers so the call does not count against the unattended quota.
    /// Debounced by <see cref="BankSyncOptions.ManualRefreshCooldown"/>; returns a report with Skipped outcomes when debounced.
    /// </summary>
    Task<SyncReport> RefreshNowAsync(PsuContext psu, CancellationToken ct = default);

    /// <summary>Runs one scheduled-style (unattended) sync immediately, respecting the quota. For hosts that drive scheduling themselves.</summary>
    Task<SyncReport> RunScheduledSyncAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SyncRunInfo>> GetSyncHistoryAsync(int take = 50, CancellationToken ct = default);
    Task<QuotaStatus> GetQuotaStatusAsync(CancellationToken ct = default);

    /// <summary>Checks that the key and application id are accepted by Enable Banking (one API call, not quota-relevant).</summary>
    Task<string> VerifyCredentialsAsync(CancellationToken ct = default);
}
