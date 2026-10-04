using System.Security.Cryptography;
using System.Text.Json;
using BankSync.Api;
using BankSync.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankSync.Core;

/// <summary>Owns the consent lifecycle: begin, complete, cancel, disconnect and status.</summary>
internal sealed class ConsentService(
    IDbContextFactory<BankSyncDbContext> dbFactory,
    EnableBankingClient api,
    SecretProtector protector,
    SyncWorkQueue queue,
    SyncEngine engine,
    IOptions<BankSyncOptions> options,
    TimeProvider time,
    IEnumerable<IBankSyncListener> listeners,
    ILogger<ConsentService> log)
{
    private Aspsp? _aspspCache;
    private DateTimeOffset _aspspCachedAt;

    public async Task<ConsentStatus> GetStatusAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var o = options.Value;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var consent = await db.Consents.Include(c => c.Accounts).ThenInclude(ca => ca.Account)
            .OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        var pending = await db.PendingAuthorizations.AnyAsync(p => p.ValidUntilUtc > now, ct);

        if (consent is null)
            return new ConsentStatus(pending ? ConsentState.Pending : ConsentState.None, null, null, NeedsRenewal: !pending, null, 0, null, false, null);

        var state = consent.Status;
        if (state is ConsentState.Active or ConsentState.Expiring)
        {
            if (consent.ValidUntilUtc <= now)
            {
                await engine.MarkConsentExpiredAsync(consent.Id, "valid_until passed", ct);
                state = ConsentState.Expired;
            }
            else if (consent.ValidUntilUtc - now <= TimeSpan.FromDays(o.RenewalWarningDays))
            {
                state = ConsentState.Expiring;
            }
        }
        if (pending && state is not (ConsentState.Active or ConsentState.Expiring)) state = ConsentState.Pending;

        var accounts = consent.Accounts.Select(ca => ca.Account).ToList();
        var daysLeft = state is ConsentState.Active or ConsentState.Expiring ? (int?)Math.Max(0, (int)Math.Floor((consent.ValidUntilUtc - now).TotalDays)) : null;
        return new ConsentStatus(
            State: state,
            ValidUntil: state is ConsentState.Active or ConsentState.Expiring ? consent.ValidUntilUtc : null,
            DaysLeft: daysLeft,
            NeedsRenewal: state is ConsentState.Expiring or ConsentState.Expired or ConsentState.Revoked or ConsentState.None,
            CreatedAt: consent.CreatedUtc,
            AccountCount: accounts.Count,
            LastSuccessfulSync: accounts.Max(a => a.LastSuccessfulSyncUtc),
            BackfillComplete: accounts.Count > 0 && accounts.All(a => a.BackfillComplete),
            LastError: accounts.Select(a => a.LastError).FirstOrDefault(e => e is not null));
    }

    public async Task<Uri> BeginAsync(PsuContext? psu, CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var aspsp = await GetAspspAsync(ct);
        var maxDays = aspsp is null ? o.ConsentDays : Math.Max(1, (int)Math.Floor(aspsp.MaximumConsentValiditySeconds / 86400.0));
        var days = Math.Min(o.ConsentDays, maxDays);
        var validUntil = now.AddDays(days).AddMinutes(-2);
        validUntil = new DateTimeOffset(validUntil.Year, validUntil.Month, validUntil.Day, validUntil.Hour, validUntil.Minute, 0, TimeSpan.Zero);

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var response = await api.StartAuthorizationAsync(new StartAuthorizationRequest(
            Access: new Access(validUntil),
            Aspsp: new AspspRef(o.AspspName, o.AspspCountry),
            State: state,
            RedirectUrl: o.RedirectUrl,
            PsuType: o.PsuType,
            Language: o.Language), ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.PendingAuthorizations.RemoveRange(db.PendingAuthorizations.Where(p => p.ValidUntilUtc <= now));
        db.PendingAuthorizations.Add(new PendingAuthorizationEntity
        {
            State = state,
            AuthorizationId = response.AuthorizationId,
            CreatedUtc = now,
            ValidUntilUtc = now + o.PendingAuthorizationTimeout,
            PsuIp = psu?.IpAddress,
            PsuUserAgent = psu?.UserAgent,
            PsuAcceptLanguage = psu?.AcceptLanguage
        });
        await db.SaveChangesAsync(ct);

        log.LogInformation("Consent started for {Aspsp}/{Country}, valid until {ValidUntil}", o.AspspName, o.AspspCountry, validUntil);
        return new Uri(response.Url);
    }

    public async Task<ConsentStatus> CompleteAsync(string code, string state, PsuContext? psu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new BankSyncConsentException("The callback did not contain a code.");
        var now = time.GetUtcNow();
        var o = options.Value;

        PendingAuthorizationEntity pending;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            pending = await db.PendingAuthorizations.FirstOrDefaultAsync(p => p.State == state, ct)
                      ?? throw new BankSyncConsentException("Unknown state parameter. Start the consent again from the app.");
            if (pending.ValidUntilUtc <= now)
            {
                db.PendingAuthorizations.Remove(pending);
                await db.SaveChangesAsync(ct);
                throw new BankSyncConsentException("The authorisation took too long. Start the consent again from the app.");
            }
        }

        var session = await api.AuthorizeSessionAsync(code, ct);
        var accounts = session.Accounts ?? [];
        var validUntil = session.Access?.ValidUntil ?? now.AddDays(o.ConsentDays);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            foreach (var old in db.Consents.Where(c => c.Status == ConsentState.Active || c.Status == ConsentState.Expiring || c.Status == ConsentState.Pending))
            {
                old.Status = ConsentState.Expired;
                old.EndedUtc = now;
                old.EndReason = "replaced by new consent";
            }

            var consent = new ConsentEntity
            {
                SessionIdProtected = protector.Protect(session.SessionId),
                AuthorizationId = pending.AuthorizationId,
                AspspName = session.Aspsp?.Name ?? o.AspspName,
                AspspCountry = session.Aspsp?.Country ?? o.AspspCountry,
                PsuType = session.PsuType ?? o.PsuType,
                CreatedUtc = now,
                ValidUntilUtc = validUntil.ToUniversalTime(),
                Status = ConsentState.Active
            };
            db.Consents.Add(consent);

            foreach (var a in accounts)
            {
                var hash = a.IdentificationHash ?? ("iban:" + (a.AccountId?.Iban ?? a.Uid));
                var account = await db.Accounts.FirstOrDefaultAsync(x => x.IdentificationHash == hash, ct);
                if (account is null)
                {
                    account = new AccountEntity { IdentificationHash = hash, FirstSeenUtc = now };
                    db.Accounts.Add(account);
                }
                account.Iban = a.AccountId?.Iban ?? account.Iban;
                account.Bban = a.AllAccountIds?.FirstOrDefault(i => string.Equals(i.SchemeName, "BBAN", StringComparison.OrdinalIgnoreCase))?.Identification
                               ?? a.AccountId?.Other?.Identification ?? account.Bban;
                account.Name = a.Name ?? account.Name;
                account.Product = a.Product ?? account.Product;
                account.Currency = a.Currency ?? account.Currency;
                account.CashAccountType = a.CashAccountType ?? account.CashAccountType;
                account.LastSeenUtc = now;
                account.RawJson = JsonSerializer.Serialize(a);
                // A new consent reopens the full-history window at the bank: run the backfill again from today.
                account.BackfillComplete = false;
                account.BackfillReachedDate = null;
                account.BackfillCompletedUtc = null;
                consent.Accounts.Add(new ConsentAccountEntity { Consent = consent, Account = account, Uid = a.Uid });
            }

            db.PendingAuthorizations.Remove(await db.PendingAuthorizations.FirstAsync(p => p.Id == pending.Id, ct));
            await db.SaveChangesAsync(ct);
        }

        log.LogInformation("Consent completed with {Count} account(s), valid until {ValidUntil}", accounts.Length, validUntil);
        if (accounts.Length == 0)
            log.LogWarning("The session returned no accounts. In Enable Banking restricted production this means the account is not linked in the control panel.");

        await NotifyAsync(l => l.OnConsentStateChangedAsync(ConsentState.Active, ct));

        var backfillPsu = psu ?? (pending.PsuIp is not null && pending.PsuUserAgent is not null
            ? new PsuContext(pending.PsuIp, pending.PsuUserAgent, pending.PsuAcceptLanguage) : null);

        if (o.EnableScheduler)
        {
            queue.Enqueue(new SyncWorkItem(SyncWorkKind.Backfill, backfillPsu, now + o.BackfillPsuWindow));
        }
        else
        {
            // No scheduler: do the initial load inline (balances + recent transactions, then history).
            await engine.RunAsync(SyncKind.Backfill, backfillPsu, ct);
            await engine.BackfillAsync(backfillPsu, ct);
        }

        return await GetStatusAsync(ct);
    }

    public async Task CancelAsync(string state, string? error, string? description, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.PendingAuthorizations.FirstOrDefaultAsync(p => p.State == state, ct);
        if (pending is not null)
        {
            db.PendingAuthorizations.Remove(pending);
            await db.SaveChangesAsync(ct);
        }
        log.LogWarning("Consent cancelled or failed: {Error} {Description}", error, description);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var consent = await db.Consents.OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        if (consent is null || consent.Status is ConsentState.Revoked) return;

        if (consent.Status is ConsentState.Active or ConsentState.Expiring)
        {
            try { await api.DeleteSessionAsync(protector.Unprotect(consent.SessionIdProtected), ct); }
            catch (EnableBankingException ex) { log.LogWarning("DELETE /sessions failed ({Code}); marking revoked locally anyway", ex.Code); }
        }
        consent.Status = ConsentState.Revoked;
        consent.EndedUtc = time.GetUtcNow();
        consent.EndReason = "disconnected by owner";
        await db.SaveChangesAsync(ct);
        await NotifyAsync(l => l.OnConsentStateChangedAsync(ConsentState.Revoked, ct));
    }

    private async Task<Aspsp?> GetAspspAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_aspspCache is not null && now - _aspspCachedAt < TimeSpan.FromHours(24)) return _aspspCache;
        var o = options.Value;
        try
        {
            var list = await api.GetAspspsAsync(o.AspspCountry, o.PsuType, ct);
            _aspspCache = list.FirstOrDefault(a => string.Equals(a.Name, o.AspspName, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Country, o.AspspCountry, StringComparison.OrdinalIgnoreCase));
            _aspspCachedAt = now;
            if (_aspspCache is null) log.LogWarning("ASPSP {Name}/{Country} not found in GET /aspsps; using configured ConsentDays", o.AspspName, o.AspspCountry);
        }
        catch (EnableBankingException ex)
        {
            log.LogWarning("GET /aspsps failed ({Code}); using configured ConsentDays", ex.Code);
        }
        return _aspspCache;
    }

    private async Task NotifyAsync(Func<IBankSyncListener, Task> action)
    {
        foreach (var l in listeners)
        {
            try { await action(l); }
            catch (Exception ex) { log.LogWarning(ex, "IBankSyncListener {Listener} threw", l.GetType().Name); }
        }
    }
}
