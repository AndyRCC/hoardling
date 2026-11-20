using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using Hoardling.Core;

namespace Hoardling.Services;

/// <summary>
/// Talks to Antigravity's local language server. Caches the endpoint and the port that answered;
/// on failure it rediscovers once. Never throws for network or format problems: returns null.
/// Used only from the coordinator loop (not thread-safe).
/// </summary>
public sealed class AntigravityClient : IDisposable
{
    private const string ServicePath = "/exa.language_server_pb.LanguageServerService/";
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;
    private string? _baseUrl;
    private string? _csrf;
    private string? _planName;
    private bool _planLoaded;
    private DateTimeOffset _nextDiscovery = DateTimeOffset.MinValue;
    private static readonly TimeSpan DiscoveryBackoff = TimeSpan.FromSeconds(15);

    public AntigravityClient(Func<DateTimeOffset> clock)
    {
        _clock = clock;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            SslOptions = new SslClientAuthenticationOptions
            {
                // The language server uses a self-signed certificate: accept it only for the loopback host.
                RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                    errors == SslPolicyErrors.None || sender is SslStream { TargetHostName: "127.0.0.1" },
            },
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ProviderLimits?> GetQuotaAsync(CancellationToken ct)
    {
        if (!_planLoaded)
        {
            _planName = await GetPlanNameAsync(ct);
        }
        var body = await CallAsync("RetrieveUserQuotaSummary", ct);
        return body is null ? null : ParseOrReset(() => AntigravityParser.ParseQuotaSummary(body, _planName, _clock()));
    }

    public async Task<AgTrajectorySnapshot?> GetActivityAsync(CancellationToken ct)
    {
        var body = await CallAsync("GetAllCascadeTrajectories", ct);
        return body is null ? null : ParseOrReset(() => AntigravityParser.ParseTrajectories(body));
    }

    public async Task<string?> GetPlanNameAsync(CancellationToken ct)
    {
        var body = await CallAsync("GetUserStatus", ct);
        if (body is null)
        {
            return null;
        }
        _planLoaded = true;
        return AntigravityParser.ParsePlanName(body);
    }

    /// <summary>Forget the cached endpoint (Antigravity closed or restarted).</summary>
    public void Reset()
    {
        _nextDiscovery = DateTimeOffset.MinValue;
        _baseUrl = null;
        _csrf = null;
        _planLoaded = false;
        _planName = null;
    }

    private async Task<string?> CallAsync(string method, CancellationToken ct)
    {
        if (_baseUrl is not null && _csrf is not null)
        {
            var cached = await TryPostAsync(_baseUrl, _csrf, method, CallTimeout, ct);
            if (cached is not null)
            {
                return cached;
            }
            Reset();
        }

        // WMI discovery is the expensive part: never run it more than once per backoff window.
        var now = _clock();
        if (now < _nextDiscovery)
        {
            return null;
        }
        _nextDiscovery = now + DiscoveryBackoff;

        foreach (var endpoint in LanguageServerLocator.FindAll())
        {
            foreach (var port in endpoint.Ports)
            {
                foreach (var scheme in (string[])["https", "http"])
                {
                    var baseUrl = $"{scheme}://127.0.0.1:{port}";
                    var body = await TryPostAsync(baseUrl, endpoint.CsrfToken, method, ProbeTimeout, ct);
                    if (body is not null)
                    {
                        _baseUrl = baseUrl;
                        _csrf = endpoint.CsrfToken;
                        _nextDiscovery = DateTimeOffset.MinValue;
                        return body;
                    }
                }
            }
        }
        return null;
    }

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>The body of a successful JSON reply, or null (any error, timeout or non-JSON answer).</summary>
    private async Task<string?> TryPostAsync(string baseUrl, string csrf, string method, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + ServicePath + method)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Add("X-Codeium-Csrf-Token", csrf);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await _http.SendAsync(request, limit.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(limit.Token);
            // Another service on a neighbouring port could answer 200 with HTML: only accept JSON objects.
            return body.TrimStart().StartsWith('{') ? body : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Parse, and forget the cached endpoint if the answer was not what we expect.</summary>
    private T? ParseOrReset<T>(Func<T> parse) where T : class
    {
        try
        {
            return parse();
        }
        catch (FormatException)
        {
            Reset();
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
