using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hoardling.Core;

public enum AgRunState { Idle, Working, NeedsYou }

public sealed record AgTrajectorySnapshot(AgRunState State, DateTimeOffset? LatestModified, IReadOnlyList<string> RawStatuses);

/// <summary>Parses the Antigravity language server's local RPC responses (undocumented).</summary>
public static partial class AntigravityParser
{
    public static ProviderLimits ParseQuotaSummary(string json, string? planName, DateTimeOffset now)
    {
        var root = JsonRead.ParseObject(json);
        var groupsNode = (root["response"] as JsonObject)?["groups"] ?? root["groups"];
        var groups = new List<LimitGroup>();
        if (groupsNode is JsonArray array)
        {
            foreach (var groupNode in array)
            {
                if (groupNode is JsonObject group)
                {
                    groups.Add(new LimitGroup(GroupName(JsonRead.String(group["displayName"])), ParseBuckets(group["buckets"])));
                }
            }
        }
        return new ProviderLimits(planName, groups, now);
    }

    private static List<LimitBucket> ParseBuckets(JsonNode? node)
    {
        var buckets = new List<LimitBucket>();
        if (node is not JsonArray array)
        {
            return buckets;
        }
        foreach (var item in array)
        {
            if (item is not JsonObject bucket)
            {
                continue;
            }
            var window = JsonRead.String(bucket["window"]);
            var (label, kind) = window switch
            {
                "5h" => ("5 h", LimitWindow.FiveHour),
                "weekly" => ("Semana", LimitWindow.Weekly),
                _ => (JsonRead.String(bucket["displayName"]) ?? window ?? "Límite", LimitWindow.Other),
            };
            // Protobuf JSON omits zero values: a missing remainingFraction means nothing is left.
            var remaining = JsonRead.Number(bucket["remainingFraction"]) ?? 0;
            var used = JsonRead.ClampPercent(100 * (1 - remaining));
            buckets.Add(new LimitBucket(label, kind, used, JsonRead.Time(bucket["resetTime"])));
        }
        return buckets;
    }

    private static string GroupName(string? displayName) => displayName switch
    {
        "Gemini Models" => "Gemini",
        "Claude and GPT models" => "Claude y GPT",
        null or "" => "Antigravity",
        _ => displayName,
    };

    public static string? ParsePlanName(string userStatusJson)
    {
        var root = JsonRead.TryParseObject(userStatusJson);
        var name = JsonRead.String(JsonRead.Path(root, "userStatus", "planStatus", "planInfo")?["planName"]);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static AgTrajectorySnapshot ParseTrajectories(string json)
    {
        var root = JsonRead.ParseObject(json);
        var statuses = new List<string>();
        DateTimeOffset? latest = null;
        if (root["trajectorySummaries"] is JsonObject summaries)
        {
            foreach (var (_, value) in summaries)
            {
                if (value is not JsonObject summary)
                {
                    continue;
                }
                statuses.Add(JsonRead.String(summary["status"]) ?? "");
                if (JsonRead.Time(summary["lastModifiedTime"]) is { } modified && (latest is null || modified > latest))
                {
                    latest = modified;
                }
            }
        }
        return new AgTrajectorySnapshot(StateFrom(statuses), latest, statuses);
    }

    // Provisional mapping, to be confirmed against live values (plan Task 14).
    private static AgRunState StateFrom(List<string> statuses)
    {
        if (statuses.Any(s => s.Contains("WAIT", StringComparison.OrdinalIgnoreCase)))
        {
            return AgRunState.NeedsYou;
        }
        if (statuses.Any(s => s.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)))
        {
            return AgRunState.Working;
        }
        return AgRunState.Idle;
    }

    public static string? ExtractCsrfToken(string commandLine)
    {
        var match = CsrfRegex().Match(commandLine);
        if (!match.Success)
        {
            return null;
        }
        var token = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["v"].Value;
        return token.Length == 0 ? null : token;
    }

    [GeneratedRegex(@"--csrf_token(?:=|\s+)(?:""(?<q>[^""]*)""|(?<v>[^\s""]+))")]
    private static partial Regex CsrfRegex();
}
