namespace Hoardling.Core;

/// <summary>
/// Smoothly blends a fixed set of ARGB colours towards a target. The first target applies instantly;
/// a new target starts from whatever is on screen right now, so changes mid-way never jump.
/// Pure and allocation-free per frame; time is in seconds from any monotonic clock.
/// </summary>
public sealed class ColorTransition
{
    private readonly uint[] _from;
    private readonly uint[] _to;
    private readonly double _duration;
    private double _start;
    private bool _initialized;

    public ColorTransition(int channels, double durationSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        Channels = channels;
        _from = new uint[channels];
        _to = new uint[channels];
        _duration = double.IsFinite(durationSeconds) && durationSeconds > 1e-6 ? durationSeconds : 1e-6;
    }

    public int Channels { get; }

    /// <summary>Smoothstep: slow at both ends, clamped to 0..1.</summary>
    public static double Ease(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    public void SetTarget(ReadOnlySpan<uint> target, double now)
    {
        if (target.Length != Channels)
        {
            throw new ArgumentException($"Expected {Channels} colours.", nameof(target));
        }
        if (!_initialized)
        {
            target.CopyTo(_from);
            target.CopyTo(_to);
            _start = double.NegativeInfinity; // already finished
            _initialized = true;
            return;
        }
        if (target.SequenceEqual(_to))
        {
            return;
        }
        Sample(now, _from); // start from what is visible now
        target.CopyTo(_to);
        _start = now;
    }

    public void Sample(double now, Span<uint> output)
    {
        var p = Ease((now - _start) / _duration);
        for (var i = 0; i < Channels; i++)
        {
            // Unchanged channels skip the OKLCh maths (half of them in any fusion change).
            output[i] = _from[i] == _to[i] || p >= 1 ? _to[i] : p <= 0 ? _from[i] : Blend(_from[i], _to[i], p);
        }
    }

    /// <summary>
    /// Blends in OKLCh (perceptual lightness, chroma and hue; hue takes the short way round), so a change
    /// like green → orange passes through yellow instead of muddy olive. Alpha blends linearly.
    /// </summary>
    private static uint Blend(uint a, uint b, double p)
    {
        var (l1, c1, h1) = ToOkLch(a);
        var (l2, c2, h2) = ToOkLch(b);
        const double greyChroma = 0.02; // below this the hue is meaningless: borrow the other one
        if (c1 < greyChroma)
        {
            h1 = h2;
        }
        if (c2 < greyChroma)
        {
            h2 = h1;
        }
        var dh = Math.IEEERemainder(h2 - h1, 2 * Math.PI);
        var l = l1 + (l2 - l1) * p;
        var c = c1 + (c2 - c1) * p;
        var h = h1 + dh * p;
        var alpha = (uint)Math.Round((a >> 24) + ((double)(b >> 24) - (a >> 24)) * p);
        return (alpha << 24) | FromOkLab(l, c * Math.Cos(h), c * Math.Sin(h));
    }

    private static (double L, double C, double H) ToOkLch(uint argb)
    {
        var r = ToLinear((argb >> 16) & 0xFF);
        var g = ToLinear((argb >> 8) & 0xFF);
        var b = ToLinear(argb & 0xFF);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        var lightness = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var labA = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var labB = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        return (lightness, Math.Sqrt(labA * labA + labB * labB), Math.Atan2(labB, labA));
    }

    private static uint FromOkLab(double lightness, double labA, double labB)
    {
        var l = Cube(lightness + 0.3963377774 * labA + 0.2158037573 * labB);
        var m = Cube(lightness - 0.1055613458 * labA - 0.0638541728 * labB);
        var s = Cube(lightness - 0.0894841775 * labA - 1.2914855480 * labB);
        var r = ToByte(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s);
        var g = ToByte(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s);
        var b = ToByte(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
        return (r << 16) | (g << 8) | b;
    }

    private static double Cube(double x) => x * x * x;

    private static double ToLinear(uint channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static uint ToByte(double linear)
    {
        var c = Math.Clamp(linear, 0, 1);
        var srgb = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (uint)Math.Round(Math.Clamp(srgb, 0, 1) * 255);
    }

    // Tolerance: 5.6 - 5.0 is 0.5999999999999996 in floating point.
    public bool IsAnimating(double now) => _initialized && now - _start < _duration - 1e-9;

}
