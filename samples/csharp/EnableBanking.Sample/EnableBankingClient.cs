using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EnableBanking.Sample;

/// <summary>
/// Thin client over the Enable Banking endpoints the app needs.
/// No business logic, no retries: the sync engine decides what to do on errors.
/// </summary>
public sealed class EnableBankingClient
{
    public const string DefaultBaseUrl = "https://api.enablebanking.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly JwtFactory _jwt;

    public EnableBankingClient(HttpClient http, JwtFactory jwt, string baseUrl = DefaultBaseUrl)
    {
        _http = http;
        _jwt = jwt;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// PSU headers mark the user as "online" and exempt the call from the 4-per-day unattended limit.
    /// Send all of them or none. Build this from the owner's own HTTP request, never from the scheduler.
    /// </summary>
    public sealed record PsuContext(string IpAddress, string UserAgent, string? AcceptLanguage = null);

    public Task<Aspsp[]> GetAspspsAsync(string? country = null, string? psuType = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (country is not null) query.Add($"country={Uri.EscapeDataString(country)}");
        if (psuType is not null) query.Add($"psu_type={Uri.EscapeDataString(psuType)}");
        var path = "aspsps" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return SendAsync<AspspsEnvelope>(HttpMethod.Get, path, body: null, psu: null, ct)
            .ContinueWith(t => t.Result.Aspsps, ct);
    }

    public Task<StartAuthorizationResponse> StartAuthorizationAsync(StartAuthorizationRequest request, CancellationToken ct = default)
        => SendAsync<StartAuthorizationResponse>(HttpMethod.Post, "auth", request, psu: null, ct);

    /// <summary>Exchanges the one-time code from the callback. Never retry this call.</summary>
    public Task<AuthorizeSessionResponse> AuthorizeSessionAsync(string code, CancellationToken ct = default)
        => SendAsync<AuthorizeSessionResponse>(HttpMethod.Post, "sessions", new AuthorizeSessionRequest(code), psu: null, ct);

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
        => SendAsync<object?>(HttpMethod.Delete, $"sessions/{Uri.EscapeDataString(sessionId)}", body: null, psu: null, ct);

    public Task<HalBalances> GetBalancesAsync(string accountUid, PsuContext? psu = null, CancellationToken ct = default)
        => SendAsync<HalBalances>(HttpMethod.Get, $"accounts/{Uri.EscapeDataString(accountUid)}/balances", body: null, psu, ct);

    public Task<HalTransactions> GetTransactionsPageAsync(
        string accountUid, DateOnly from, DateOnly to, string? continuationKey = null, PsuContext? psu = null, CancellationToken ct = default)
    {
        var path = $"accounts/{Uri.EscapeDataString(accountUid)}/transactions?date_from={from:yyyy-MM-dd}&date_to={to:yyyy-MM-dd}";
        if (!string.IsNullOrEmpty(continuationKey))
            path += "&continuation_key=" + Uri.EscapeDataString(continuationKey);
        return SendAsync<HalTransactions>(HttpMethod.Get, path, body: null, psu, ct);
    }

    /// <summary>Follows continuation_key until the last page. Each page is one API call for quota purposes.</summary>
    public async IAsyncEnumerable<Transaction> GetTransactionsAsync(
        string accountUid, DateOnly from, DateOnly to, PsuContext? psu = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? key = null;
        do
        {
            var page = await GetTransactionsPageAsync(accountUid, from, to, key, psu, ct);
            foreach (var tx in page.Transactions) yield return tx;
            key = page.ContinuationKey;
        } while (!string.IsNullOrEmpty(key));
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, PsuContext? psu, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt.GetToken());
        if (psu is not null)
        {
            request.Headers.TryAddWithoutValidation("Psu-Ip-Address", psu.IpAddress);
            request.Headers.TryAddWithoutValidation("Psu-User-Agent", psu.UserAgent);
            if (psu.AcceptLanguage is not null)
                request.Headers.TryAddWithoutValidation("Psu-Accept-Language", psu.AcceptLanguage);
        }
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode)
        {
            if (typeof(T) == typeof(object) || response.StatusCode == HttpStatusCode.NoContent)
                return default!;
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))
                   ?? throw new EnableBankingException(response.StatusCode, "EMPTY_RESPONSE", "Empty response body");
        }

        ApiError? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ApiError>(Json, ct); } catch { /* non-JSON error body */ }
        throw new EnableBankingException(response.StatusCode, error?.Error ?? response.StatusCode.ToString(), error?.Message ?? response.ReasonPhrase ?? "");
    }

    private sealed record AspspsEnvelope([property: System.Text.Json.Serialization.JsonPropertyName("aspsps")] Aspsp[] Aspsps);
}

public sealed class EnableBankingException(HttpStatusCode status, string code, string message)
    : Exception($"{(int)status} {code}: {message}")
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;

    public bool IsRateLimited => Status == HttpStatusCode.TooManyRequests;
    public bool IsSessionExpired => Code is "EXPIRED_SESSION" or "SESSION_CLOSED" or "SESSION_EXPIRED";
    public bool IsAuthProblem => Status == HttpStatusCode.Unauthorized;
    public bool IsTransient => (int)Status >= 500;
}
