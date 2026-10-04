using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BankSync.Tests;

/// <summary>In-memory stand-in for api.enablebanking.com. Serves canned JSON and records every request.</summary>
public sealed partial class FakeEnableBanking : HttpMessageHandler
{
    public sealed record Call(string Method, string Path, bool HasPsuHeaders, string? Query);
    public sealed record FakeTx(string? EntryReference, string Status, DateOnly Booking, decimal Amount, string Text, string? Counterparty = null);

    public List<Call> Calls { get; } = [];
    public List<FakeTx> Transactions { get; } = [];
    public string AccountUid { get; set; } = "acc-1";
    public string Iban { get; set; } = "DK5000400440116243";
    public string IdentificationHash { get; set; } = "hash-1";
    public long MaxConsentSeconds { get; set; } = 15552000;
    public DateTimeOffset? SessionValidUntil { get; set; }
    public int PageSize { get; set; } = int.MaxValue;
    public decimal Balance { get; set; } = 1000m;

    /// <summary>The test's clock. Set by TestHost so the fake bank and the library agree on "now" (never use the real clock here).</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => throw new InvalidOperationException("Set FakeEnableBanking.Now to the test clock.");

    /// <summary>Return a response to short-circuit a request, or null to use the default behaviour.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public int CountCalls(string method, string pathPrefix) => Calls.Count(c => c.Method == method && c.Path.StartsWith(pathPrefix));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var query = request.RequestUri.Query;
        var hasPsu = request.Headers.Contains("Psu-Ip-Address");
        Calls.Add(new Call(request.Method.Method, path, hasPsu, query));

        if (request.Headers.Authorization?.Scheme != "Bearer" || string.IsNullOrEmpty(request.Headers.Authorization.Parameter))
            return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"error":"UNAUTHORIZED","message":"missing token"}"""));

        // PSU headers must be all or none
        var psuIp = request.Headers.Contains("Psu-Ip-Address");
        var psuUa = request.Headers.Contains("Psu-User-Agent");
        if (psuIp != psuUa)
            return Task.FromResult(Json(HttpStatusCode.UnprocessableEntity, """{"error":"PSU_HEADER_NOT_PROVIDED","message":"partial psu headers"}"""));

        if (Override?.Invoke(request) is { } overridden) return Task.FromResult(overridden);

        return Task.FromResult(Route(request.Method, path, query));
    }

    private HttpResponseMessage Route(HttpMethod method, string path, string query)
    {
        if (method == HttpMethod.Get && path == "application")
            return Json(HttpStatusCode.OK, """{"name":"test-app","environment":"SANDBOX","active":true,"redirect_urls":["https://localhost/callback"]}""");

        if (method == HttpMethod.Get && path == "aspsps")
            return Json(HttpStatusCode.OK, $$"""{"aspsps":[{"name":"Nordea","country":"DK","logo":"https://x/logo.png","psu_types":["personal","business"],"auth_methods":[],"maximum_consent_validity":{{MaxConsentSeconds}},"beta":false,"bic":"NDEADKKK","required_psu_headers":["psu-ip-address","psu-user-agent"]}]}""");

        if (method == HttpMethod.Post && path == "auth")
            return Json(HttpStatusCode.OK, """{"url":"https://fake.enablebanking.test/consent/abc","authorization_id":"auth-1"}""");

        if (method == HttpMethod.Post && path == "sessions")
        {
            var validUntil = (SessionValidUntil ?? Now().AddDays(180)).ToString("o");
            return Json(HttpStatusCode.OK, $$$"""
            {"session_id":"sess-1","psu_type":"personal","aspsp":{"name":"Nordea","country":"DK"},
             "access":{"valid_until":"{{{validUntil}}}","balances":true,"transactions":true},
             "accounts":[{"uid":"{{{AccountUid}}}","account_id":{"iban":"{{{Iban}}}","other":{"identification":"0040-0440116243","scheme_name":"BBAN"}},
                          "all_account_ids":[{"identification":"{{{Iban}}}","scheme_name":"IBAN"},{"identification":"0040-0440116243","scheme_name":"BBAN"}],
                          "name":"Lønkonto","product":"Private","currency":"DKK","cash_account_type":"CACC","usage":"PRIV","identification_hash":"{{{IdentificationHash}}}"}]}
            """);
        }

        if (method == HttpMethod.Delete && path.StartsWith("sessions/"))
            return new HttpResponseMessage(HttpStatusCode.NoContent);

        if (method == HttpMethod.Get && path == $"accounts/{AccountUid}/balances")
            return Json(HttpStatusCode.OK, $$"""{"balances":[{"name":"Booked","balance_amount":{"currency":"DKK","amount":"{{Balance.ToString(System.Globalization.CultureInfo.InvariantCulture)}}"},"balance_type":"CLBD","reference_date":"2026-10-01"},{"name":"Available","balance_amount":{"currency":"DKK","amount":"{{(Balance - 100).ToString(System.Globalization.CultureInfo.InvariantCulture)}}"},"balance_type":"ITAV"}]}""");

        if (method == HttpMethod.Get && path == $"accounts/{AccountUid}/transactions")
        {
            var from = DateOnly.Parse(QueryParam().Match(query + "&").Groups["from"].Value);
            var to = DateOnly.Parse(QueryParamTo().Match(query + "&").Groups["to"].Value);
            var keyMatch = Regex.Match(query, @"continuation_key=(\d+)");
            var start = keyMatch.Success ? int.Parse(keyMatch.Groups[1].Value) : 0;

            var inRange = Transactions.Where(t => t.Booking >= from && t.Booking <= to).OrderByDescending(t => t.Booking).ToList();
            var page = inRange.Skip(start).Take(PageSize).ToList();
            var next = start + page.Count < inRange.Count ? (start + page.Count).ToString() : null;

            var items = page.Select(t => new Dictionary<string, object?>
            {
                ["entry_reference"] = t.EntryReference,
                ["transaction_amount"] = new { currency = "DKK", amount = Math.Abs(t.Amount).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) },
                ["credit_debit_indicator"] = t.Amount < 0 ? "DBIT" : "CRDT",
                ["status"] = t.Status,
                ["booking_date"] = t.Booking.ToString("yyyy-MM-dd"),
                ["value_date"] = t.Booking.ToString("yyyy-MM-dd"),
                ["creditor"] = t.Amount < 0 ? new { name = t.Counterparty ?? "SHOP" } : null,
                ["debtor"] = t.Amount >= 0 ? new { name = t.Counterparty ?? "EMPLOYER" } : null,
                ["remittance_information"] = new[] { t.Text },
                ["balance_after_transaction"] = new { currency = "DKK", amount = "0.00" }
            });
            var body = JsonSerializer.Serialize(new { transactions = items, continuation_key = next });
            return Json(HttpStatusCode.OK, body);
        }

        return Json(HttpStatusCode.NotFound, """{"error":"NOT_FOUND","message":"no route"}""");
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [GeneratedRegex(@"date_from=(?<from>[0-9-]+)")]
    private static partial Regex QueryParam();

    [GeneratedRegex(@"date_to=(?<to>[0-9-]+)")]
    private static partial Regex QueryParamTo();
}
