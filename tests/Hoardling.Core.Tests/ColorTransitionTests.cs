using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class ColorTransitionTests
{
    private const uint Mint = 0xFF8FD1A8;
    private const uint Orange = 0xFFF28C52;
    private const uint Blue = 0xFF5B9BE6;

    private static uint[] Sample(ColorTransition t, double now)
    {
        var output = new uint[t.Channels];
        t.Sample(now, output);
        return output;
    }

    [Fact]
    public void FirstTarget_AppliesInstantly()
    {
        var t = new ColorTransition(1, 0.6);

        t.SetTarget([Mint], now: 10);

        Assert.Equal([Mint], Sample(t, 10));
        Assert.False(t.IsAnimating(10));
    }

    [Fact]
    public void Halfway_IsTheMidpointColor()
    {
        var t = new ColorTransition(1, 0.6);
        t.SetTarget([0xFF000000], 0);

        t.SetTarget([0xFFFFFFFF], 1);

        // Smoothstep at 0.5 is 0.5; blended in OKLab, perceptual mid-grey is L = 0.5 → sRGB 99 (0x63).
        Assert.Equal([0xFF636363u], Sample(t, 1.3));
        Assert.True(t.IsAnimating(1.3));
    }

    [Fact]
    public void MintToOrange_MidpointStaysVivid()
    {
        // Plain RGB blending passes through a muddy olive (channel spread 67). OKLCh keeps it colourful.
        var t = new ColorTransition(1, 1.0);
        t.SetTarget([Mint], 0);
        t.SetTarget([Orange], 0);

        var mid = Sample(t, 0.5)[0];
        var r = (mid >> 16) & 0xFF;
        var g = (mid >> 8) & 0xFF;
        var b = mid & 0xFF;

        Assert.True(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 90, $"midpoint {mid:X8} is too grey");
        Assert.True(r > b && g > b, $"midpoint {mid:X8} should be warm (yellowish)");
    }

    [Theory]
    [InlineData(0xFF8FD1A8u)]
    [InlineData(0xFFF28C52u)]
    [InlineData(0xFF5B9BE6u)]
    [InlineData(0xFFB7C4BCu)]
    [InlineData(0xFF000000u)]
    [InlineData(0xFFFFFFFFu)]
    public void Endpoints_AreExact(uint color)
    {
        var t = new ColorTransition(1, 1.0);
        t.SetTarget([0xFF336699], 0);
        t.SetTarget([color], 0);

        Assert.Equal([color], Sample(t, 1.0));
    }

    [Fact]
    public void Eased_SlowAtStartAndEnd()
    {
        Assert.Equal(0, ColorTransition.Ease(0));
        Assert.Equal(1, ColorTransition.Ease(1));
        Assert.True(ColorTransition.Ease(0.1) < 0.1);
        Assert.True(ColorTransition.Ease(0.9) > 0.9);
        Assert.Equal(1, ColorTransition.Ease(1.5));
        Assert.Equal(0, ColorTransition.Ease(-1));
    }

    [Fact]
    public void AfterDuration_ExactlyTargetAndDone()
    {
        var t = new ColorTransition(2, 0.6);
        t.SetTarget([Mint, Mint], 0);

        t.SetTarget([Orange, Blue], 5);

        Assert.Equal([Orange, Blue], Sample(t, 5.6));
        Assert.False(t.IsAnimating(5.6));
    }

    [Fact]
    public void Retarget_MidTransition_StartsFromCurrentColor()
    {
        var t = new ColorTransition(1, 1.0);
        t.SetTarget([0xFF000000], 0);
        t.SetTarget([0xFFFFFFFF], 0);
        var mid = Sample(t, 0.5)[0];

        t.SetTarget([0xFF000000], 0.5);

        Assert.Equal(mid, Sample(t, 0.5)[0]); // no jump
        Assert.Equal([0xFF000000u], Sample(t, 1.5));
    }

    [Fact]
    public void SameTarget_DoesNotRestart()
    {
        var t = new ColorTransition(1, 0.6);
        t.SetTarget([Mint], 0);
        t.SetTarget([Orange], 1);

        t.SetTarget([Orange], 1.3);

        Assert.False(t.IsAnimating(1.6));
        Assert.Equal([Orange], Sample(t, 1.6));
    }

    [Fact]
    public void AlphaIsBlendedToo()
    {
        var t = new ColorTransition(1, 1.0);
        t.SetTarget([0x00FFFFFF], 0);

        t.SetTarget([0xFFFFFFFF], 0);

        Assert.Equal(0x80u, Sample(t, 0.5)[0] >> 24);
    }

    [Fact]
    public void BeforeAnyTarget_IsSafe()
    {
        var t = new ColorTransition(2, 0.6);

        Assert.False(t.IsAnimating(0));
        Assert.Equal([0u, 0u], Sample(t, 0));
    }

    [Fact]
    public void HueWrap_RedToMagenta_TakesTheShortArc()
    {
        // Red (hue ~29°) → magenta (~328°): the short way passes through pink, not green or yellow.
        var t = new ColorTransition(1, 1.0);
        t.SetTarget([0xFFFF0000], 0);
        t.SetTarget([0xFFFF00FF], 0);

        var mid = Sample(t, 0.5)[0];
        var g = (mid >> 8) & 0xFF;

        Assert.True(g < 0x60, $"midpoint {mid:X8} went the long way (too much green)");
    }

    [Fact]
    public void UnchangedChannel_StaysExactDuringTheBlend()
    {
        var t = new ColorTransition(2, 1.0);
        t.SetTarget([Mint, 0xFFEEF2EC], 0);
        t.SetTarget([Orange, 0xFFEEF2EC], 0);

        Assert.Equal(0xFFEEF2ECu, Sample(t, 0.5)[1]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-1)]
    public void OddDurations_DoNotProduceGarbage(double duration)
    {
        var t = new ColorTransition(1, duration);
        t.SetTarget([Mint], 0);
        t.SetTarget([Orange], 0);

        Assert.Equal([Orange], Sample(t, 0.01));
    }

    [Fact]
    public void WrongChannelCount_Throws()
    {
        var t = new ColorTransition(2, 0.6);

        Assert.Throws<ArgumentException>(() => t.SetTarget([Mint], 0));
    }
}
