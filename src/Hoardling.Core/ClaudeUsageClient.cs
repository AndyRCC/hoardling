using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Hoardling.Core;

public sealed record ClaudeUsageResult(ClaudeDataStatus Status, ProviderLimits? Limits, TimeSpan? RetryAfter, bool RateLimited);

/// <summary>
/// Fetches Claude plan usage with Claude Code's OAuth token, refreshing it when expired.
/// Rules that protect the user's Claude Code session:
/// - Only a rejected refresh grant (400/401) means "log in again"; anything else is a temporary error.
/// - Before giving up, re-read the file: Claude Code may have renewed the token meanwhile.
/// - A renewed (rotated) token that could not be saved is kept in memory and saved later, never lost.
/// After a rejected grant it stays quiet until the credentials file changes (the user ran /login).
/// Not thread-safe: coordinator loop only.
/// </summary>
public sealed class ClaudeUsageClient(HttpClient http, CredentialsStore store, Func<DateTimeOffset> clock)
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string BetaHeader = "oauth-2025-04-20";
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private enum RefreshOutcome { Renewed, Rejected, Failed }

    private bool _blocked;
    private DateTime? _blockedStamp;
    private ClaudeCredentials? _unsaved;
    private string? _unsavedReplaces;

    public async Task<ClaudeUsageResult> FetchAsync(CancellationToken ct)
    {
        try
        {
            var creds = CurrentCredentials();
            if (creds is null)
            {
                return Result(ClaudeDataStatus.NoCredentials);
            }
            if (_blocked)
            {
                if (store.LastWriteTimeUtc == _blockedStamp)
                {
                    return Result(ClaudeDataStatus.NeedsLogin);
                }
                _blocked = false;
            }

            var refreshed = false;
            if (IsExpired(creds))
            {
                var (outcome, renewed, failure) = await RefreshAsync(creds, ct);
                switch (outcome)
                {
                    case RefreshOutcome.Renewed:
                        creds = renewed!;
                        refreshed = true;
                        break;
                    case RefreshOutcome.Rejected:
                        return await RecoverOrBlockAsync(creds, ct);
                    default:
                        return failure!;
                }
            }

            using var response = await GetUsageAsync(creds.AccessToken, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return await InterpretAsync(response, creds, ct);
            }

            // Rejected token: maybe another process already renewed the file; otherwise refresh once.
            var latest = store.Read();
            if (latest is not null && latest.AccessToken != creds.AccessToken && !IsExpired(latest))
            {
                creds = latest;
            }
            else if (!refreshed)
            {
                var (outcome, renewed, failure) = await RefreshAsync(latest ?? creds, ct);
                switch (outcome)
                {
                    case RefreshOutcome.Renewed:
                        creds = renewed!;
                        break;
                    case RefreshOutcome.Rejected:
                        return await RecoverOrBlockAsync(latest ?? creds, ct);
                    default:
                        return failure!;
                }
            }
            else
            {
                return Block(store.LastWriteTimeUtc);
            }

            using var retry = await GetUsageAsync(creds.AccessToken, ct);
            return retry.StatusCode == HttpStatusCode.Unauthorized ? Block(store.LastWriteTimeUtc) : await InterpretAsync(retry, creds, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or UnauthorizedAccessException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Result(ClaudeDataStatus.Error);
        }
    }

    /// <summary>File credentials, unless we hold newer renewed tokens that are not saved yet.</summary>
    private ClaudeCredentials? CurrentCredentials()
    {
        var fromFile = store.Read();
        if (_unsaved is null)
        {
            return fromFile;
        }
        if (fromFile is not null && fromFile.RefreshToken != _unsavedReplaces)
        {
            // Someone else (e.g. /login) wrote newer credentials: the file wins.
            _unsaved = null;
            return fromFile;
        }
        var pending = _unsaved;
        if (TrySave(pending))
        {
            _unsaved = null;
        }
        return pending;
    }

    private async Task<ClaudeUsageResult> RecoverOrBlockAsync(ClaudeCredentials used, CancellationToken ct)
    {
        var stamp = store.LastWriteTimeUtc;
        var latest = store.Read();
        if (latest is null || latest.RefreshToken == used.RefreshToken || IsExpired(latest))
        {
            return Block(stamp);
        }
        using var response = await GetUsageAsync(latest.AccessToken, ct);
        return response.StatusCode == HttpStatusCode.Unauthorized ? Block(stamp) : await InterpretAsync(response, latest, ct);
    }

    private bool IsExpired(ClaudeCredentials creds) => creds.ExpiresAt - ExpirySkew <= clock();

    private async Task<HttpResponseMessage> GetUsageAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", BetaHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await http.SendAsync(request, ct);
    }

    private async Task<(RefreshOutcome, ClaudeCredentials?, ClaudeUsageResult?)> RefreshAsync(ClaudeCredentials creds, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = creds.RefreshToken,
            ["client_id"] = ClientId,
        };
        if (!string.IsNullOrEmpty(creds.Scope))
        {
            body["scope"] = creds.Scope;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            return (RefreshOutcome.Rejected, null, null);
        }
        if (!response.IsSuccessStatusCode)
        {
            var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
            return (RefreshOutcome.Failed, null, new ClaudeUsageResult(ClaudeDataStatus.Error, null, RetryAfter(response), rateLimited));
        }
        var json = JsonRead.TryParseObject(await response.Content.ReadAsStringAsync(ct));
        var access = JsonRead.String(json?["access_token"]);
        if (string.IsNullOrEmpty(access))
        {
            return (RefreshOutcome.Failed, null, Result(ClaudeDataStatus.Error));
        }
        var refresh = JsonRead.String(json!["refresh_token"]);
        var expiresIn = Math.Clamp(JsonRead.Number(json["expires_in"]) ?? 3600, 60, TimeSpan.FromDays(365).TotalSeconds);
        var renewed = creds with
        {
            AccessToken = access,
            RefreshToken = string.IsNullOrEmpty(refresh) ? creds.RefreshToken : refresh,
            ExpiresAt = clock().AddSeconds(expiresIn),
        };
        if (!TrySave(renewed))
        {
            _unsaved = renewed;
            _unsavedReplaces = creds.RefreshToken;
        }
        return (RefreshOutcome.Renewed, renewed, null);
    }

    private bool TrySave(ClaudeCredentials creds)
    {
        try
        {
            store.WriteTokens(creds.AccessToken, creds.RefreshToken, creds.ExpiresAt);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    private async Task<ClaudeUsageResult> InterpretAsync(HttpResponseMessage response, ClaudeCredentials creds, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new ClaudeUsageResult(ClaudeDataStatus.Error, null, RetryAfter(response), true);
        }
        if (!response.IsSuccessStatusCode)
        {
            return Result(ClaudeDataStatus.Error);
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        var limits = ClaudeUsageParser.Parse(text, ClaudeUsageParser.PlanFromSubscription(creds.SubscriptionType), clock());
        return new ClaudeUsageResult(ClaudeDataStatus.Ok, limits, null, false);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - clock() : null);

    private ClaudeUsageResult Block(DateTime? stamp)
    {
        _blocked = true;
        _blockedStamp = stamp;
        return Result(ClaudeDataStatus.NeedsLogin);
    }

    private static ClaudeUsageResult Result(ClaudeDataStatus status) => new(status, null, null, false);
}
