namespace Hoardling.Core;

public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;

    public double OverlapArea(RectD other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>Pure placement rules for the pet and its card, in device-independent pixels.</summary>
public static class WindowPlacement
{
    public const double Margin = 12;
    public const double CardGap = 8;

    /// <summary>
    /// Saved position if at least half the pet is visible; pulled inside if less; bottom-right of the
    /// primary work area if it is not visible at all (e.g. the monitor it was on is gone).
    /// </summary>
    public static (double X, double Y) Resolve(double? left, double? top, double w, double h, IReadOnlyList<RectD> workAreas, RectD primary)
    {
        var fallback = (primary.Right - w - Margin, primary.Bottom - h - Margin);
        if (left is not { } x || top is not { } y)
        {
            return fallback;
        }
        var pet = new RectD(x, y, w, h);
        RectD? best = null;
        var bestArea = 0.0;
        foreach (var area in workAreas)
        {
            var overlap = pet.OverlapArea(area);
            if (overlap > bestArea)
            {
                bestArea = overlap;
                best = area;
            }
        }
        if (best is not { } screen)
        {
            return fallback;
        }
        if (bestArea >= w * h / 2)
        {
            return (x, y);
        }
        return (Math.Clamp(x, screen.X, screen.Right - w), Math.Clamp(y, screen.Y, screen.Bottom - h));
    }

    /// <summary>Card to the left of the pet if it fits, otherwise to the right; top-aligned and kept on screen.</summary>
    public static (double X, double Y) CardPosition(RectD pet, double cardW, double cardH, RectD workArea)
    {
        var x = pet.X - cardW - CardGap;
        if (x < workArea.X)
        {
            x = Math.Min(pet.Right + CardGap, workArea.Right - cardW);
        }
        var y = Math.Max(workArea.Y, Math.Min(pet.Y, workArea.Bottom - cardH));
        return (x, y);
    }
}
