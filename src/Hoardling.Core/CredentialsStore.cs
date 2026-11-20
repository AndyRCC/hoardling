using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hoardling.Core;

/// <param name="Scope">Space-separated OAuth scopes, sent back on refresh exactly like Claude Code does.</param>
public sealed record ClaudeCredentials(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? SubscriptionType, string? Scope = null)
{
    /// <summary>Never print tokens, even by accident in a log or exception message.</summary>
    public override string ToString() => $"ClaudeCredentials {{ ExpiresAt = {ExpiresAt:u}, SubscriptionType = {SubscriptionType} }}";
}

/// <summary>Reads and updates Claude Code's <c>~/.claude/.credentials.json</c>, preserving every other field.</summary>
public sealed class CredentialsStore(string path)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Path { get; } = path;

    public DateTime? LastWriteTimeUtc => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null;

    /// <summary>
    /// Null when the file is missing or unusable. A locked or unreadable file throws
    /// (<see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>): that is temporary, not "logged out".
    /// </summary>
    public ClaudeCredentials? Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        var oauth = JsonRead.TryParseObject(text)?["claudeAiOauth"] as JsonObject;
        var access = JsonRead.String(oauth?["accessToken"]);
        var refresh = JsonRead.String(oauth?["refreshToken"]);
        var expires = JsonRead.Number(oauth?["expiresAt"]);
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || expires is not { } ms
            || ms < MinUnixMs || ms > MaxUnixMs)
        {
            return null;
        }
        var scopes = (oauth!["scopes"] as JsonArray)?.Select(JsonRead.String).OfType<string>().ToArray();
        return new ClaudeCredentials(access, refresh, DateTimeOffset.FromUnixTimeMilliseconds((long)ms),
            JsonRead.String(oauth["subscriptionType"]), scopes is { Length: > 0 } ? string.Join(' ', scopes) : null);
    }

    private static readonly double MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly double MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>Re-reads the file and replaces only the three token fields, atomically.</summary>
    public void WriteTokens(string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        var root = JsonRead.ParseObject(File.ReadAllText(Path));
        var oauth = root["claudeAiOauth"] as JsonObject ?? throw new FormatException("claudeAiOauth missing.");
        oauth["accessToken"] = accessToken;
        oauth["refreshToken"] = refreshToken;
        oauth["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
        AtomicFile.WriteAllText(Path, root.ToJsonString(WriteOptions));
    }
}
