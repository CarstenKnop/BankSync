namespace BankSync;

/// <summary>
/// Configuration for the BankSync library. Bind it from configuration section "BankSync"
/// or set it in code via <c>services.AddBankSync(o =&gt; ...)</c>.
/// </summary>
public sealed class BankSyncOptions
{
    public const string SectionName = "BankSync";

    // --- Enable Banking application -------------------------------------------------

    /// <summary>Application ID from the Enable Banking control panel. Required.</summary>
    public string ApplicationId { get; set; } = "";

    /// <summary>Path to the RSA private key (PEM) matching the certificate registered at Enable Banking. Either this or <see cref="PrivateKeyPem"/> is required.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The private key as PEM text. Prefer <see cref="PrivateKeyPath"/> with a secret file; use this only when the host injects secrets in memory.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Enable Banking API base URL.</summary>
    public string BaseUrl { get; set; } = "https://api.enablebanking.com";

    /// <summary>The redirect URL registered at Enable Banking. The host app must route it to <see cref="IBankSync.CompleteConsentAsync"/>. Required.</summary>
    public string RedirectUrl { get; set; } = "";

    /// <summary>Lifetime of the JWTs the library mints. Must be below 24 hours.</summary>
    public TimeSpan JwtLifetime { get; set; } = TimeSpan.FromMinutes(30);

    // --- Bank (ASPSP) and user -------------------------------------------------------

    /// <summary>ASPSP name as listed by Enable Banking. Default "Nordea".</summary>
    public string AspspName { get; set; } = "Nordea";

    /// <summary>ASPSP country code. Default "DK".</summary>
    public string AspspCountry { get; set; } = "DK";

    /// <summary>"personal" or "business".</summary>
    public string PsuType { get; set; } = "personal";

    /// <summary>Two-letter language code for the consent pages, e.g. "da". Null lets Enable Banking choose.</summary>
    public string? Language { get; set; } = "da";

    /// <summary>Requested consent length. Clamped to the ASPSP's maximum (180 days for Nordea DK).</summary>
    public int ConsentDays { get; set; } = 180;

    /// <summary>A pending authorisation (between BeginConsent and CompleteConsent) is discarded after this time.</summary>
    public TimeSpan PendingAuthorizationTimeout { get; set; } = TimeSpan.FromMinutes(15);

    // --- Storage ---------------------------------------------------------------------

    /// <summary>Path of the SQLite database file. The directory is created if missing. Default "banksync.db" in the working directory.</summary>
    public string DatabasePath { get; set; } = "banksync.db";

    /// <summary>Path of the AES key file used to encrypt the Enable Banking session id at rest. Default: next to the database as "banksync.key".</summary>
    public string? SecretKeyPath { get; set; }

    // --- Sync behaviour --------------------------------------------------------------

    /// <summary>Run the background scheduler. Set false in tests or if the host triggers syncs itself.</summary>
    public bool EnableScheduler { get; set; } = true;

    /// <summary>
    /// Daily sync times, local to <see cref="TimeZone"/>, "HH:mm". At most <see cref="MaxUnattendedCallsPerDay"/> entries.
    /// Null (the default) means 06:30, 11:30, 16:30 and 21:30. It is deliberately not pre-filled: the configuration
    /// binder appends to a pre-filled array instead of replacing it, which would double the slots.
    /// </summary>
    public string[]? SyncSlots { get; set; }

    internal static readonly string[] DefaultSyncSlots = ["06:30", "11:30", "16:30", "21:30"];

    internal string[] EffectiveSyncSlots => SyncSlots ?? DefaultSyncSlots;

    /// <summary>IANA time zone id used for slots and the daily quota day boundary.</summary>
    public string TimeZone { get; set; } = "Europe/Copenhagen";

    /// <summary>Maximum unattended (no PSU headers) API calls per account, per endpoint kind, per local day. PSD2 allows 4.</summary>
    public int MaxUnattendedCallsPerDay { get; set; } = 4;

    /// <summary>Each incremental fetch starts this many days before the last booked transaction, to catch late bookings.</summary>
    public int OverlapDays { get; set; } = 5;

    /// <summary>Minimum time between two manual (PSU-present) refreshes.</summary>
    public TimeSpan ManualRefreshCooldown { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How far back the backfill tries to go after a new consent.</summary>
    public int BackfillMaxYears { get; set; } = 10;

    /// <summary>Length of each backfill window in days.</summary>
    public int BackfillWindowDays { get; set; } = 365;

    /// <summary>The owner's PSU headers captured at consent are reused for backfill calls this long (Enable Banking gives full history for about an hour).</summary>
    public TimeSpan BackfillPsuWindow { get; set; } = TimeSpan.FromMinutes(55);

    /// <summary><see cref="ConsentStatus.NeedsRenewal"/> becomes true this many days before expiry.</summary>
    public int RenewalWarningDays { get; set; } = 14;

    /// <summary>Jitter added to each account's slot time so that accounts are not fetched at the same second.</summary>
    public TimeSpan MaxSlotJitter { get; set; } = TimeSpan.FromMinutes(4);

    internal string ResolveSecretKeyPath()
        => SecretKeyPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabasePath)) ?? ".", "banksync.key");

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationId)) throw new BankSyncConfigurationException($"{nameof(ApplicationId)} is required.");
        if (string.IsNullOrWhiteSpace(PrivateKeyPath) && string.IsNullOrWhiteSpace(PrivateKeyPem)) throw new BankSyncConfigurationException($"{nameof(PrivateKeyPath)} or {nameof(PrivateKeyPem)} is required.");
        if (string.IsNullOrWhiteSpace(RedirectUrl)) throw new BankSyncConfigurationException($"{nameof(RedirectUrl)} is required.");
        if (JwtLifetime <= TimeSpan.Zero || JwtLifetime > TimeSpan.FromHours(24)) throw new BankSyncConfigurationException($"{nameof(JwtLifetime)} must be between 0 and 24 hours.");
        var slots = EffectiveSyncSlots;
        if (slots.Length > MaxUnattendedCallsPerDay) throw new BankSyncConfigurationException($"{nameof(SyncSlots)} has {slots.Length} entries but {nameof(MaxUnattendedCallsPerDay)} is {MaxUnattendedCallsPerDay}.");
        foreach (var s in slots)
            if (!TimeOnly.TryParseExact(s, "HH:mm", out _)) throw new BankSyncConfigurationException($"Sync slot '{s}' is not in HH:mm format.");
        try { TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        catch (Exception ex) { throw new BankSyncConfigurationException($"Unknown time zone '{TimeZone}'.", ex); }
        if (ConsentDays <= 0) throw new BankSyncConfigurationException($"{nameof(ConsentDays)} must be positive.");
    }
}

public class BankSyncException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class BankSyncConfigurationException(string message, Exception? inner = null) : BankSyncException(message, inner);
/// <summary>Thrown by consent operations when the state is wrong (unknown state, expired pending authorisation, no active consent).</summary>
public sealed class BankSyncConsentException(string message, Exception? inner = null) : BankSyncException(message, inner);
