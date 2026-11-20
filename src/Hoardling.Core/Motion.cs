namespace Hoardling.Core;

/// <summary>Small, allocation-free easing helpers for frame-by-frame animation.</summary>
public static class Motion
{
    /// <summary>Frame-rate independent exponential approach: after one time constant, 63 % of the way.</summary>
    // All helpers are NaN-safe: comparisons are written so NaN takes the "do nothing" branch,
    // because one NaN stored in animation state would stay there for good.

    public static double Approach(double current, double target, double dt, double timeConstant)
    {
        if (!(dt > 0) || !double.IsFinite(target))
        {
            return current;
        }
        if (!(timeConstant > 0))
        {
            return target;
        }
        return target + (current - target) * Math.Exp(-dt / timeConstant);
    }

    /// <summary>0 → 1 → 0 over p in [0, 1]; 0 outside.</summary>
    public static double Bell(double p) => p > 0 && p < 1 ? Math.Sin(Math.PI * p) : 0;

    /// <summary>Ease-out with a small overshoot (a "pop"), clamped to [0, 1] input.</summary>
    public static double OutBack(double p)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        if (!(p > 0))
        {
            return 0;
        }
        if (p >= 1)
        {
            return 1;
        }
        var x = p - 1;
        return 1 + c3 * x * x * x + c1 * x * x;
    }

    /// <summary>Local progress of p inside [start, end], clamped to [0, 1].</summary>
    public static double Window(double p, double start, double end)
    {
        if (double.IsNaN(p))
        {
            return 0;
        }
        return end <= start ? (p >= end ? 1 : 0) : Math.Clamp((p - start) / (end - start), 0, 1);
    }
}
