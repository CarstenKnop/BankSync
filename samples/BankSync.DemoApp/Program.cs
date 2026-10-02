// Minimal host that shows how an app uses BankSync. No login, no styling: the point is the IBankSync calls.
// Configure via appsettings.json / user secrets / environment (BankSync__ApplicationId, BankSync__PrivateKeyPath, BankSync__RedirectUrl, BankSync__DatabasePath).

using System.Net;
using System.Text;
using BankSync;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBankSync(builder.Configuration);
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();
app.UseForwardedHeaders();

static PsuContext Psu(HttpContext http) => new(
    http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0",
    http.Request.Headers.UserAgent.ToString(),
    http.Request.Headers.AcceptLanguage.ToString());

app.MapGet("/", async (IBankSync bank) =>
{
    var status = await bank.GetConsentStatusAsync();
    var html = new StringBuilder("<!doctype html><meta name=viewport content='width=device-width'><title>BankSync demo</title><body style='font-family:system-ui;max-width:40rem;margin:1rem auto;padding:0 1rem'>");
    html.Append($"<h1>BankSync demo</h1><p>Consent: <b>{status.State}</b>");
    if (status.ValidUntil is { } vu) html.Append($" until {vu:yyyy-MM-dd} ({status.DaysLeft} days left)");
    html.Append($"<br>Backfill complete: {status.BackfillComplete}<br>Last sync: {status.LastSuccessfulSync?.ToString("u") ?? "never"}");
    if (status.LastError is not null) html.Append($"<br>Last error: {WebUtility.HtmlEncode(status.LastError)}");
    html.Append("</p>");
    html.Append(status.NeedsRenewal
        ? "<form method=post action=/connect><button>Connect / renew with MitID</button></form>"
        : "<form method=post action=/refresh><button>Refresh now</button></form> <form method=post action=/disconnect><button>Disconnect</button></form>");

    foreach (var a in await bank.GetAccountsAsync())
    {
        var balances = await bank.GetBalancesAsync(a.Id);
        var booked = balances.FirstOrDefault(b => b.BalanceType == "CLBD") ?? balances.FirstOrDefault();
        html.Append($"<h2>{WebUtility.HtmlEncode(a.DisplayName ?? a.Name ?? a.MaskedIban)}</h2><p>{a.MaskedIban} · {booked?.Amount.ToString("N2")} {booked?.Currency} · {a.EarliestTransactionDate}…{a.LatestTransactionDate}</p>");
        var page = await bank.GetTransactionsAsync(new TransactionQuery { AccountId = a.Id, Take = 20 });
        html.Append("<table style='width:100%;border-collapse:collapse'>");
        foreach (var t in page.Items)
            html.Append($"<tr style='border-bottom:1px solid #ddd'><td>{t.BookingDate:yyyy-MM-dd}{(t.IsPending ? " <i>(pending)</i>" : "")}</td><td>{WebUtility.HtmlEncode(t.CounterpartyName ?? "")}<br><small>{WebUtility.HtmlEncode(t.Text ?? "")}</small></td><td style='text-align:right;color:{(t.IsDebit ? "#b00" : "#070")}'>{t.Amount:N2}</td></tr>");
        html.Append($"</table><p>{page.Total} transactions</p>");
    }

    var quota = await bank.GetQuotaStatusAsync();
    html.Append($"<h3>Quota {quota.LocalDay}</h3><ul>");
    foreach (var q in quota.Accounts) html.Append($"<li>account {q.AccountId}: balances {q.BalancesCallsUsed}/{quota.Limit}, transactions {q.TransactionsCallsUsed}/{quota.Limit}{(q.RateLimitedToday ? " (429 today)" : "")}</li>");
    html.Append("</ul><h3>Last sync runs</h3><ul>");
    foreach (var r in (await bank.GetSyncHistoryAsync(10)).Where(r => r.AccountId is null))
        html.Append($"<li>{r.StartedAt:u} {r.Kind} {(r.Unattended ? "unattended" : "psu")} → {r.Outcome}, +{r.TransactionsNew}/~{r.TransactionsUpdated}{(r.Error is null ? "" : " " + WebUtility.HtmlEncode(r.Error))}</li>");
    html.Append("</ul></body>");
    return Results.Content(html.ToString(), "text/html; charset=utf-8");
});

app.MapPost("/connect", async (IBankSync bank, HttpContext http) =>
    Results.Redirect((await bank.BeginConsentAsync(Psu(http))).ToString()));

app.MapGet("/callback", async (IBankSync bank, HttpContext http, string? code, string? state, string? error, string? error_description) =>
{
    if (state is null) return Results.BadRequest("missing state");
    if (error is not null || code is null)
    {
        await bank.CancelConsentAsync(state, error, error_description);
        return Results.Content($"Consent failed: {WebUtility.HtmlEncode(error ?? "no code")} {WebUtility.HtmlEncode(error_description ?? "")}. <a href=/>Back</a>", "text/html");
    }
    try
    {
        await bank.CompleteConsentAsync(code, state, Psu(http));
        return Results.Redirect("/");
    }
    catch (BankSyncConsentException ex)
    {
        return Results.Content($"{WebUtility.HtmlEncode(ex.Message)} <a href=/>Back</a>", "text/html");
    }
});

app.MapPost("/refresh", async (IBankSync bank, HttpContext http) => { await bank.RefreshNowAsync(Psu(http)); return Results.Redirect("/"); });
app.MapPost("/disconnect", async (IBankSync bank) => { await bank.DisconnectAsync(); return Results.Redirect("/"); });

app.MapGet("/api/accounts", (IBankSync bank) => bank.GetAccountsAsync());
app.MapGet("/api/accounts/{id:int}/balances", (IBankSync bank, int id) => bank.GetBalancesAsync(id));
app.MapGet("/api/transactions", (IBankSync bank, int? accountId, DateOnly? from, DateOnly? to, string? text, int skip = 0, int take = 50) =>
    bank.GetTransactionsAsync(new TransactionQuery { AccountId = accountId, From = from, To = to, Text = text, Skip = skip, Take = take }));
app.MapGet("/api/summary", (IBankSync bank, int? accountId, int months = 12) => bank.GetMonthlySummaryAsync(accountId, months));
app.MapGet("/api/status", (IBankSync bank) => bank.GetConsentStatusAsync());
app.MapGet("/healthz", () => Results.Ok());

app.Run();
