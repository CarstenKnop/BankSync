namespace BankSync.Data;

internal sealed class ConsentEntity
{
    public int Id { get; set; }
    public string SessionIdProtected { get; set; } = "";
    public string? AuthorizationId { get; set; }
    public string AspspName { get; set; } = "";
    public string AspspCountry { get; set; } = "";
    public string PsuType { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ValidUntilUtc { get; set; }
    public ConsentState Status { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    public string? EndReason { get; set; }
    public List<ConsentAccountEntity> Accounts { get; set; } = [];
}

internal sealed class PendingAuthorizationEntity
{
    public int Id { get; set; }
    public string State { get; set; } = "";
    public string? AuthorizationId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ValidUntilUtc { get; set; }
    public string? PsuIp { get; set; }
    public string? PsuUserAgent { get; set; }
    public string? PsuAcceptLanguage { get; set; }
}

internal sealed class AccountEntity
{
    public int Id { get; set; }
    public string IdentificationHash { get; set; } = "";
    public string? Iban { get; set; }
    public string? Bban { get; set; }
    public string? Name { get; set; }
    public string? Product { get; set; }
    public string? Currency { get; set; }
    public string? CashAccountType { get; set; }
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public string? RawJson { get; set; }

    // Backfill progress
    public bool BackfillComplete { get; set; }
    public DateOnly? BackfillReachedDate { get; set; }
    public DateTimeOffset? BackfillCompletedUtc { get; set; }

    // Sync bookkeeping
    public DateOnly? LastBookedDate { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public string? LastError { get; set; }

    public List<ConsentAccountEntity> Consents { get; set; } = [];
}

internal sealed class ConsentAccountEntity
{
    public int ConsentId { get; set; }
    public ConsentEntity Consent { get; set; } = null!;
    public int AccountId { get; set; }
    public AccountEntity Account { get; set; } = null!;
    public string Uid { get; set; } = "";
}

internal sealed class TransactionEntity
{
    public long Id { get; set; }
    public int AccountId { get; set; }
    public string DedupHash { get; set; } = "";
    public string? EntryReference { get; set; }
    public string? TransactionId { get; set; }
    public string Status { get; set; } = "BOOK";
    public DateOnly? BookingDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    public DateOnly? TransactionDate { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? CreditDebit { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyAccount { get; set; }
    public string? Text { get; set; }
    public string? TextNormalized { get; set; }
    public string? BankTransactionCode { get; set; }
    public decimal? BalanceAfter { get; set; }
    public string? MerchantCategoryCode { get; set; }
    public string RawJson { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }

    // User data, never overwritten by sync
    public string? Category { get; set; }
    public string? Note { get; set; }
    public string? Tags { get; set; }
}

internal sealed class BalanceSnapshotEntity
{
    public long Id { get; set; }
    public int AccountId { get; set; }
    public DateTimeOffset FetchedUtc { get; set; }
    public string BalanceType { get; set; } = "";
    public string? Name { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateOnly? ReferenceDate { get; set; }
}

internal sealed class SyncRunEntity
{
    public long Id { get; set; }
    public int? AccountId { get; set; }
    public SyncKind Kind { get; set; }
    public bool Unattended { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public SyncOutcome Outcome { get; set; }
    public int CallsBalances { get; set; }
    public int CallsTransactions { get; set; }
    public int TransactionsNew { get; set; }
    public int TransactionsUpdated { get; set; }
    public string? Error { get; set; }
}

/// <summary>One row per API call to a data endpoint; the source of truth for the daily quota.</summary>
internal sealed class ApiCallEntity
{
    public long Id { get; set; }
    public int AccountId { get; set; }
    /// <summary>Local day (in the configured time zone) as yyyy-MM-dd.</summary>
    public string LocalDay { get; set; } = "";
    public CallKind Kind { get; set; }
    public bool Unattended { get; set; }
    public DateTimeOffset CalledUtc { get; set; }
    public int StatusCode { get; set; }
}

internal enum CallKind { Balances = 1, Transactions = 2 }

internal sealed class SettingEntity
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}
