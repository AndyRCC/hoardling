namespace Hoardling.Core;

/// <summary>Ordered from shortest to longest: <see cref="ProviderLimits.RingPercent"/> relies on it.</summary>
public enum LimitWindow { FiveHour, Weekly, Other }

public sealed record LimitBucket(string Label, LimitWindow Window, double UsedPercent, DateTimeOffset? ResetsAt);

public sealed record LimitGroup(string Name, IReadOnlyList<LimitBucket> Buckets);

public sealed record ProviderLimits(string? PlanName, IReadOnlyList<LimitGroup> Groups, DateTimeOffset FetchedAt)
{
    public const double ExhaustedThreshold = 99.5;

    private IEnumerable<LimitBucket> AllBuckets => Groups.SelectMany(g => g.Buckets);

    /// <summary>Highest usage (0–100) among buckets of the given window, or null if there are none.</summary>
    public double? MaxUsed(LimitWindow window)
    {
        double? max = null;
        foreach (var bucket in AllBuckets)
        {
            if (bucket.Window == window && (max is null || bucket.UsedPercent > max))
            {
                max = bucket.UsedPercent;
            }
        }
        return max;
    }

    /// <summary>
    /// What the ring shows: the most used of each group's shortest limit. A group with a 5-hour
    /// limit counts with it and its weekly one is left to the card; a group whose only limit is
    /// weekly (Gemini 4 Argon on Antigravity's Pro plan) counts with that, so using it still
    /// moves the ring. Null when there are no limits at all.
    /// </summary>
    public double? RingPercent()
    {
        double? max = null;
        foreach (var group in Groups)
        {
            if (group.Buckets.Count == 0)
            {
                continue;
            }
            var shortest = group.Buckets.Min(b => b.Window);
            var used = group.Buckets.Where(b => b.Window == shortest).Max(b => b.UsedPercent);
            if (max is null || used > max)
            {
                max = used;
            }
        }
        return max;
    }

    /// <summary>True when any bucket (any window) is used up, which blocks the provider.</summary>
    public bool AnyExhausted => AllBuckets.Any(b => b.UsedPercent >= ExhaustedThreshold);
}
