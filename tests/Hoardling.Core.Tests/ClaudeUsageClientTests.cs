using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hoardling.Core;

namespace Hoardling.Core.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Auth, string? Beta, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        request.Headers.TryGetValues("anthropic-beta", out var beta);
        Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), beta?.FirstOrDefault(), body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class ClaudeUsageClientTests : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string UsageOk = """{"five_hour":{"utilization":38,"resets_at":"2026-11-19T04:00:00Z"},"seven_day":{"utilization":21}}""";
    private static readonly DateTimeOffset Now = TestData.T0;

    private readonly TempDir _dir = new();
    private readonly string _path;

    public ClaudeUsageClientTests() => _path = _dir.File(".credentials.json");

    public void Dispose() => _dir.Dispose();

    private void WriteCreds(string access, string refresh, DateTimeOffset expires) =>
        File.WriteAllText(_path, CredentialsStoreTests.CredentialsJson(access, refresh, expires.ToUnixTimeMilliseconds()));

    private ClaudeUsageClient Client(FakeHandler handler) =>
        new(new HttpClient(handler), new CredentialsStore(_path), () => Now);

    [Fact]
    public async Task Client_ValidToken_GetsUsage()
    {
        WriteCreds("A", "R", Now.AddHours(2));
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, UsageOk));

        var result = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.Ok, result.Status);
        Assert.Equal(38, result.Limits!.MaxUsed(LimitWindow.FiveHour));
        Assert.Equal("Pro", result.Limits.PlanName);
        var req = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Get, UsageUrl, "Bearer A", "oauth-2025-04-20"), (req.Method, req.Url, req.Auth, req.Beta));
    }

    [Fact]
    public async Task Client_Expired_RefreshesThenFetches()
    {
        WriteCreds("A", "R", Now.AddMinutes(-5));
        var handler = new FakeHandler((req, _) => req.RequestUri!.ToString() == TokenUrl
            ? FakeHandler.Json(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2","expires_in":28800}""")
            : FakeHandler.Json(HttpStatusCode.OK, UsageOk));

        var result = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.Ok, result.Status);
        Assert.Equal(2, handler.Requests.Count);
        var refresh = handler.Requests[0];
        Assert.Equal((HttpMethod.Post, TokenUrl), (refresh.Method, refresh.Url));
        var body = JsonNode.Parse(refresh.Body!)!;
        Assert.Equal("refresh_token", body["grant_type"]!.GetValue<string>());
        Assert.Equal("R", body["refresh_token"]!.GetValue<string>());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", body["client_id"]!.GetValue<string>());
        Assert.Equal("user:inference user:profile", body["scope"]!.GetValue<string>());
        Assert.Equal("Bearer A2", handler.Requests[1].Auth);
        var saved = new CredentialsStore(_path).Read()!;
        Assert.Equal(("A2", "R2", Now.AddSeconds(28800)), (saved.AccessToken, saved.RefreshToken, saved.ExpiresAt));
    }

    [Fact]
    public async Task Client_RefreshWithoutNewRefreshToken_KeepsOld()
    {
        WriteCreds("A", "R", Now.AddMinutes(-5));
        var handler = new FakeHandler((req, _) => req.RequestUri!.ToString() == TokenUrl
            ? FakeHandler.Json(HttpStatusCode.OK, """{"access_token":"A2","expires_in":3600}""")
            : FakeHandler.Json(HttpStatusCode.OK, UsageOk));

        await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal("R", new CredentialsStore(_path).Read()!.RefreshToken);
    }

    [Fact]
    public async Task Client_RefreshFails_NeedsLogin_AndDoesNotRetryUntilFileChanges()
    {
        WriteCreds("A", "R", Now.AddMinutes(-5));
        var loggedInAgain = false;
        var handler = new FakeHandler((_, _) => loggedInAgain
            ? FakeHandler.Json(HttpStatusCode.OK, UsageOk)
            : FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        var client = Client(handler);

        var first = await client.FetchAsync(CancellationToken.None);
        var second = await client.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.NeedsLogin, first.Status);
        Assert.Equal(ClaudeDataStatus.NeedsLogin, second.Status);
        Assert.Single(handler.Requests);

        // The user runs /login: the file changes and the same client tries again.
        WriteCreds("A3", "R3", Now.AddHours(1));
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(1));
        loggedInAgain = true;
        var third = await client.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.Ok, third.Status);
        Assert.Equal("Bearer A3", handler.Requests[^1].Auth);
    }

    [Fact]
    public async Task Client_Usage401_RefreshOnceThenNeedsLogin()
    {
        WriteCreds("A", "R", Now.AddHours(2));
        var handler = new FakeHandler((req, _) => req.RequestUri!.ToString() == TokenUrl
            ? FakeHandler.Json(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2","expires_in":3600}""")
            : FakeHandler.Json(HttpStatusCode.Unauthorized, "{}"));

        var result = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.NeedsLogin, result.Status);
        Assert.Equal(["GET", "POST", "GET"], handler.Requests.Select(r => r.Method.Method));
    }

    [Fact]
    public async Task Client_RereadsFileBeforeRefresh()
    {
        WriteCreds("A", "R", Now.AddHours(2));
        var handler = new FakeHandler((req, _) =>
        {
            if (req.Headers.Authorization?.Parameter == "A")
            {
                WriteCreds("B", "RB", Now.AddHours(3)); // another process renewed meanwhile
                return FakeHandler.Json(HttpStatusCode.Unauthorized, "{}");
            }
            return FakeHandler.Json(HttpStatusCode.OK, UsageOk);
        });

        var result = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.Ok, result.Status);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("Bearer B", handler.Requests[^1].Auth);
    }

    [Fact]
    public async Task Client_429_RateLimited()
    {
        WriteCreds("A", "R", Now.AddHours(2));
        var handler = new FakeHandler((_, _) =>
        {
            var r = FakeHandler.Json(HttpStatusCode.TooManyRequests, "{}");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return r;
        });

        var result = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal((ClaudeDataStatus.Error, true, TimeSpan.FromSeconds(120)), (result.Status, result.RateLimited, result.RetryAfter));
    }

    [Fact]
    public async Task Client_ServerErrorOrBadJson_Error()
    {
        WriteCreds("A", "R", Now.AddHours(2));
        var bad = await Client(new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, "not json"))).FetchAsync(CancellationToken.None);
        var down = await Client(new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.BadGateway, ""))).FetchAsync(CancellationToken.None);
        var net = await Client(new FakeHandler((_, _) => throw new HttpRequestException("offline"))).FetchAsync(CancellationToken.None);

        Assert.Equal([ClaudeDataStatus.Error, ClaudeDataStatus.Error, ClaudeDataStatus.Error], new[] { bad.Status, down.Status, net.Status });
    }

    [Fact]
    public async Task Client_WriteFailsAfterRefresh_KeepsRenewedTokensAndPersistsLater()
    {
        // Review finding 2: losing a rotated refresh token would log the user out of Claude Code.
        WriteCreds("A", "R", Now.AddMinutes(-5));
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        var handler = new FakeHandler((req, _) => req.RequestUri!.ToString() == TokenUrl
            ? FakeHandler.Json(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2","expires_in":3600}""")
            : FakeHandler.Json(HttpStatusCode.OK, UsageOk));
        var client = Client(handler);

        var first = await client.FetchAsync(CancellationToken.None);
        File.SetAttributes(_path, FileAttributes.Normal);
        var second = await client.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.Ok, first.Status);
        Assert.Equal(ClaudeDataStatus.Ok, second.Status);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("Bearer A2", handler.Requests[^1].Auth);
        var saved = new CredentialsStore(_path).Read()!;
        Assert.Equal(("A2", "R2"), (saved.AccessToken, saved.RefreshToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Client_RefreshTransientFailure_ErrorNotLogin(HttpStatusCode code)
    {
        // Review finding 1: only a rejected grant means "log in again".
        WriteCreds("A", "R", Now.AddMinutes(-5));
        var handler = new FakeHandler((_, _) => FakeHandler.Json(code, "{}"));
        var client = Client(handler);

        var first = await client.FetchAsync(CancellationToken.None);
        await client.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.Error, first.Status);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Client_ConcurrentRefreshByClaudeCode_Recovers()
    {
        // Review finding 3: Claude Code renewed first, our refresh with the old token is rejected.
        WriteCreds("A", "R", Now.AddMinutes(-5));
        var handler = new FakeHandler((req, _) =>
        {
            if (req.RequestUri!.ToString() == TokenUrl)
            {
                WriteCreds("A2", "R2", Now.AddHours(8));
                return FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
            }
            return FakeHandler.Json(HttpStatusCode.OK, UsageOk);
        });

        var result = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.Ok, result.Status);
        Assert.Equal("Bearer A2", handler.Requests[^1].Auth);
    }

    [Fact]
    public async Task Client_CredentialsLocked_TransientError()
    {
        // Review finding 9: a locked file is not "no credentials".
        WriteCreds("A", "R", Now.AddHours(2));
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await Client(new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, UsageOk))).FetchAsync(CancellationToken.None);

            Assert.Equal(ClaudeDataStatus.Error, result.Status);
        }
    }

    [Fact]
    public async Task Client_RefreshWithAbsurdExpiresIn_DoesNotThrow()
    {
        WriteCreds("A", "R", Now.AddMinutes(-5));
        var handler = new FakeHandler((req, _) => req.RequestUri!.ToString() == TokenUrl
            ? FakeHandler.Json(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2","expires_in":1e20}""")
            : FakeHandler.Json(HttpStatusCode.OK, UsageOk));

        var result = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Client_CallerCancellation_Propagates()
    {
        WriteCreds("A", "R", Now.AddHours(2));
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler((_, _) =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).FetchAsync(cts.Token));
    }

    [Fact]
    public async Task Client_NoFile_NoCredentials()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("no request expected"));

        var result = await Client(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeDataStatus.NoCredentials, result.Status);
        Assert.Empty(handler.Requests);
    }
}
