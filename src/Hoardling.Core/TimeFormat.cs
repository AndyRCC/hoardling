namespace Hoardling.Core;

public static class TimeFormat
{
    /// <summary>Compact Spanish countdown such as "2h 10m", "45m", "3d 4h", "&lt;1m" or "ya".</summary>
    public static string Until(DateTimeOffset target, DateTimeOffset now)
    {
        var left = target - now;
        if (left <= TimeSpan.Zero)
        {
            return "ya";
        }
        if (left < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }
        if (left.TotalDays >= 1)
        {
            return left.Hours == 0 ? $"{left.Days}d" : $"{left.Days}d {left.Hours}h";
        }
        if (left.TotalHours >= 1)
        {
            return left.Minutes == 0 ? $"{left.Hours}h" : $"{left.Hours}h {left.Minutes}m";
        }
        return $"{left.Minutes}m";
    }
}
