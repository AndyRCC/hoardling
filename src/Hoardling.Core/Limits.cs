namespace Hoardling.Core;

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

    /// <summary>True when any bucket (any window) is used up, which blocks the provider.</summary>
    public bool AnyExhausted => AllBuckets.Any(b => b.UsedPercent >= ExhaustedThreshold);
}
