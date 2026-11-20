using System.Text.Json.Nodes;

namespace Hoardling.Core;

/// <summary>Parses the response of GET https://api.anthropic.com/api/oauth/usage.</summary>
public static class ClaudeUsageParser
{
    private static readonly (string Key, string Label, LimitWindow Window)[] Fields =
    [
        ("five_hour", "Sesión 5 h", LimitWindow.FiveHour),
        ("seven_day", "Semana", LimitWindow.Weekly),
        ("seven_day_opus", "Semana · Opus", LimitWindow.Weekly),
        ("seven_day_sonnet", "Semana · Sonnet", LimitWindow.Weekly),
    ];

    public static ProviderLimits Parse(string json, string? planName, DateTimeOffset now)
    {
        var root = JsonRead.ParseObject(json);
        var buckets = new List<LimitBucket>();
        foreach (var (key, label, window) in Fields)
        {
            if (root[key] is JsonObject entry && JsonRead.Number(entry["utilization"]) is { } used)
            {
                buckets.Add(new LimitBucket(label, window, JsonRead.ClampPercent(used), JsonRead.Time(entry["resets_at"])));
            }
        }
        return new ProviderLimits(planName, [new LimitGroup("Claude", buckets)], now);
    }

    /// <summary>"pro" → "Pro", "max" → "Max"; null or empty → null.</summary>
    public static string? PlanFromSubscription(string? subscriptionType) =>
        string.IsNullOrWhiteSpace(subscriptionType)
            ? null
            : char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..];
}
