using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankSync.Api;

/// <summary>Thin client over the Enable Banking endpoints. No retries, no business logic; errors become <see cref="EnableBankingException"/>.</summary>
internal sealed class EnableBankingClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly JwtFactory _jwt;
    private readonly ILogger<EnableBankingClient> _log;

    public EnableBankingClient(HttpClient http, JwtFactory jwt, IOptions<BankSyncOptions> options, ILogger<EnableBankingClient> log)
    {
        _http = http;
        _jwt = jwt;
        _log = log;
        _http.BaseAddress = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<ApplicationInfo> GetApplicationAsync(CancellationToken ct)
        => SendAsync<ApplicationInfo>(HttpMethod.Get, "application", null, null, ct);

    public async Task<Aspsp[]> GetAspspsAsync(string? country, string? psuType, CancellationToken ct)
    {
        var query = new List<string>();
        if (country is not null) query.Add("country=" + Uri.EscapeDataString(country));
        if (psuType is not null) query.Add("psu_type=" + Uri.EscapeDataString(psuType));
        var env = await SendAsync<AspspsEnvelope>(HttpMethod.Get, "aspsps" + (query.Count > 0 ? "?" + string.Join("&", query) : ""), null, null, ct);
        return env.Aspsps;
    }

    public Task<StartAuthorizationResponse> StartAuthorizationAsync(StartAuthorizationRequest request, CancellationToken ct)
        => SendAsync<StartAuthorizationResponse>(HttpMethod.Post, "auth", request, null, ct);

    /// <summary>Single-use code; never retried.</summary>
    public Task<AuthorizeSessionResponse> AuthorizeSessionAsync(string code, CancellationToken ct)
        => SendAsync<AuthorizeSessionResponse>(HttpMethod.Post, "sessions", new AuthorizeSessionRequest(code), null, ct);

    public Task<GetSessionResponse> GetSessionAsync(string sessionId, CancellationToken ct)
        => SendAsync<GetSessionResponse>(HttpMethod.Get, "sessions/" + Uri.EscapeDataString(sessionId), null, null, ct);

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct)
        => SendAsync<object?>(HttpMethod.Delete, "sessions/" + Uri.EscapeDataString(sessionId), null, null, ct);

    public Task<HalBalances> GetBalancesAsync(string accountUid, PsuContext? psu, CancellationToken ct)
        => SendAsync<HalBalances>(HttpMethod.Get, $"accounts/{Uri.EscapeDataString(accountUid)}/balances", null, psu, ct);

    public Task<HalTransactions> GetTransactionsPageAsync(string accountUid, DateOnly from, DateOnly to, string? continuationKey, PsuContext? psu, CancellationToken ct)
    {
        var path = $"accounts/{Uri.EscapeDataString(accountUid)}/transactions?date_from={from:yyyy-MM-dd}&date_to={to:yyyy-MM-dd}";
        if (!string.IsNullOrEmpty(continuationKey)) path += "&continuation_key=" + Uri.EscapeDataString(continuationKey);
        return SendAsync<HalTransactions>(HttpMethod.Get, path, null, psu, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, PsuContext? psu, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt.GetToken());
        if (psu is not null)
        {
            // All or none: Enable Banking rejects a partial set with PSU_HEADER_NOT_PROVIDED.
            request.Headers.TryAddWithoutValidation("Psu-Ip-Address", psu.IpAddress);
            request.Headers.TryAddWithoutValidation("Psu-User-Agent", psu.UserAgent);
            if (!string.IsNullOrWhiteSpace(psu.AcceptLanguage))
                request.Headers.TryAddWithoutValidation("Psu-Accept-Language", psu.AcceptLanguage);
        }
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new EnableBankingException(HttpStatusCode.ServiceUnavailable, "NETWORK_ERROR", ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new EnableBankingException(HttpStatusCode.GatewayTimeout, "TIMEOUT", "The request timed out.", ex);
        }

        using (response)
        {
            _log.LogDebug("Enable Banking {Method} {Path} -> {Status}", method, StripQuery(path), (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(object) || response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                    return default!;
                return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                       ?? throw new EnableBankingException(response.StatusCode, "EMPTY_RESPONSE", "Empty response body");
            }

            ApiError? error = null;
            try { error = await response.Content.ReadFromJsonAsync<ApiError>(Json, ct); } catch { /* non-JSON body */ }
            throw new EnableBankingException(response.StatusCode, error?.Error ?? response.StatusCode.ToString(), error?.Message ?? response.ReasonPhrase ?? "");
        }
    }

    private static string StripQuery(string path) => path.Split('?')[0];
}

/// <summary>An error response from Enable Banking or a transport failure.</summary>
public sealed class EnableBankingException(HttpStatusCode status, string code, string message, Exception? inner = null)
    : BankSyncException($"{(int)status} {code}: {message}", inner)
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;

    public bool IsRateLimited => Status == HttpStatusCode.TooManyRequests;
    public bool IsSessionExpired => Code.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase) || Code.Contains("SESSION_CLOSED", StringComparison.OrdinalIgnoreCase);
    public bool IsAuthProblem => Status == HttpStatusCode.Unauthorized;
    public bool IsForbidden => Status == HttpStatusCode.Forbidden;
    public bool IsTransient => (int)Status >= 500 || Code is "NETWORK_ERROR" or "TIMEOUT";
}
