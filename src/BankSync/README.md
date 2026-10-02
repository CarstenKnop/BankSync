# BankSync

Self-hosted account data sync for [Enable Banking](https://enablebanking.com) (PSD2).
BankSync handles everything between your app and the bank, so your app only ever reads a local SQLite database:

- RS256 JWT authentication with your Enable Banking private key
- the consent flow (start, callback, renewal, disconnect)
- full-history **backfill** right after consent, while the bank still exposes it
- scheduled incremental sync inside the PSD2 **four-unattended-calls-per-day** quota
- quota-exempt manual refresh with PSU headers
- deduplication, pending-to-booked replacement, user notes and categories that survive re-sync
- SQLite storage via EF Core, session id encrypted at rest

It defaults to **Nordea Denmark** (personal accounts) but works with any ASPSP Enable Banking lists.
Read-only: it never initiates payments.

## Install

```bash
dotnet add package BankSync
```

## Use

```csharp
builder.Services.AddBankSync(o =>
{
    o.ApplicationId  = builder.Configuration["BankSync:ApplicationId"]!;
    o.PrivateKeyPath = "/run/secrets/eb_private_key";
    o.RedirectUrl    = "https://your.host/callback";
    o.DatabasePath   = "/data/banksync.db";
    // o.AspspName = "Nordea"; o.AspspCountry = "DK"; o.PsuType = "personal";  // defaults
});
```

```csharp
app.MapPost("/connect", async (IBankSync bank, HttpContext http) =>
    Results.Redirect((await bank.BeginConsentAsync(Psu(http))).ToString()));

app.MapGet("/callback", async (IBankSync bank, HttpContext http, string? code, string state, string? error, string? error_description) =>
{
    if (error is not null) { await bank.CancelConsentAsync(state, error, error_description); return Results.Redirect("/?error=" + error); }
    await bank.CompleteConsentAsync(code!, state, Psu(http));
    return Results.Redirect("/");
});

app.MapGet("/api/transactions", (IBankSync bank, int? accountId, int skip = 0, int take = 50) =>
    bank.GetTransactionsAsync(new TransactionQuery { AccountId = accountId, Skip = skip, Take = take }));

static PsuContext Psu(HttpContext http) => new(
    http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0",
    http.Request.Headers.UserAgent.ToString(),
    http.Request.Headers.AcceptLanguage.ToString());
```

Everything else (`GetAccountsAsync`, `GetBalancesAsync`, `GetConsentStatusAsync`, `RefreshNowAsync`,
`GetMonthlySummaryAsync`, `GetQuotaStatusAsync`, `GetSyncHistoryAsync`) is on `IBankSync`.

## What happens when

| Event | BankSync does |
|---|---|
| Host starts | Creates the database, runs the slot that was missed today if any, continues a pending backfill |
| `BeginConsentAsync` | `GET /aspsps` (cached), `POST /auth`, stores the `state`, returns the bank's URL |
| `CompleteConsentAsync` | `POST /sessions`, stores accounts, queues a backfill that reuses the owner's PSU headers for 55 minutes so it is quota-exempt |
| Each configured slot (default 06:30, 11:30, 16:30, 21:30 Europe/Copenhagen) | Balances + incremental transactions per account, at most 4 unattended calls per endpoint per account per day; continues backfill with any quota left |
| HTTP 429 | Marks the account rate-limited for today; next attempt at the next slot. No retry loops. |
| `EXPIRED_SESSION` | Consent becomes `Expired`; `ConsentStatus.NeedsRenewal` is true; scheduler goes idle |
| `RefreshNowAsync(psu)` | Fetches now with PSU headers (not counted), debounced to one per 10 minutes |

## Configuration keys

See `BankSyncOptions`. All keys bind from the `BankSync` section
(`BankSync__ApplicationId`, `BankSync__PrivateKeyPath`, `BankSync__RedirectUrl`, `BankSync__DatabasePath`,
`BankSync__SyncSlots__0` …).

## Status

0.1.0. The schema is created with `EnsureCreated`; migrations will be introduced before 1.0.
Requires .NET 10.

## Licence

MIT.
