using System.Text.Json.Nodes;
using Hoardling.Core;

namespace Hoardling.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hoardling-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class CredentialsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static string CredentialsJson(string access = "A", string refresh = "R", long expiresAtMs = 1781846308293) =>
        new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = access,
                ["refreshToken"] = refresh,
                ["expiresAt"] = expiresAtMs,
                ["scopes"] = new JsonArray("user:inference", "user:profile"),
                ["subscriptionType"] = "pro",
                ["rateLimitTier"] = "default_claude_ai",
            },
            ["organizationUuid"] = "org-1",
            ["mcpOAuth"] = new JsonObject { ["x"] = new JsonObject { ["serverName"] = "x", ["accessToken"] = "" } },
        }.ToJsonString();

    [Fact]
    public void Store_ReadsFields()
    {
        var path = _dir.File(".credentials.json");
        File.WriteAllText(path, CredentialsJson());

        var creds = new CredentialsStore(path).Read();

        Assert.NotNull(creds);
        Assert.Equal("A", creds.AccessToken);
        Assert.Equal("R", creds.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1781846308293), creds.ExpiresAt);
        Assert.Equal("pro", creds.SubscriptionType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("""{"claudeAiOauth":{}}""")]
    [InlineData("""{"other":1}""")]
    public void Store_MissingOrInvalid_Null(string? content)
    {
        var path = _dir.File(".credentials.json");
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }

        Assert.Null(new CredentialsStore(path).Read());
    }

    [Fact]
    public void Store_WriteTokens_PreservesOtherFields()
    {
        var path = _dir.File(".credentials.json");
        File.WriteAllText(path, CredentialsJson());
        var expires = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

        new CredentialsStore(path).WriteTokens("A2", "R2", expires);

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var oauth = root["claudeAiOauth"]!;
        Assert.Equal("A2", oauth["accessToken"]!.GetValue<string>());
        Assert.Equal("R2", oauth["refreshToken"]!.GetValue<string>());
        Assert.Equal(expires.ToUnixTimeMilliseconds(), oauth["expiresAt"]!.GetValue<long>());
        Assert.Equal("pro", oauth["subscriptionType"]!.GetValue<string>());
        Assert.Equal(2, oauth["scopes"]!.AsArray().Count);
        Assert.Equal("org-1", root["organizationUuid"]!.GetValue<string>());
        Assert.Equal("x", root["mcpOAuth"]!["x"]!["serverName"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1e20)]
    [InlineData(-1e20)]
    public void Store_OutOfRangeExpiresAt_Null(double expires)
    {
        var path = _dir.File(".credentials.json");
        var json = JsonNode.Parse(CredentialsJson())!;
        json["claudeAiOauth"]!["expiresAt"] = expires;
        File.WriteAllText(path, json.ToJsonString());

        Assert.Null(new CredentialsStore(path).Read());
    }

    [Fact]
    public void Store_LockedFile_Throws()
    {
        var path = _dir.File(".credentials.json");
        File.WriteAllText(path, CredentialsJson());

        using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => new CredentialsStore(path).Read());
    }

    [Fact]
    public void Credentials_ToString_HidesTokens()
    {
        var creds = new ClaudeCredentials("secret-access", "secret-refresh", DateTimeOffset.UnixEpoch, "pro");

        var text = creds.ToString();

        Assert.DoesNotContain("secret-access", text);
        Assert.DoesNotContain("secret-refresh", text);
    }

    [Fact]
    public void Store_LastWriteTime()
    {
        var path = _dir.File(".credentials.json");
        var store = new CredentialsStore(path);
        Assert.Null(store.LastWriteTimeUtc);

        File.WriteAllText(path, CredentialsJson());

        Assert.NotNull(store.LastWriteTimeUtc);
    }
}
