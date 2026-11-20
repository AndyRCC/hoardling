using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class MotionTests
{
    [Fact]
    public void Approach_OneTimeConstant_Covers63Percent()
    {
        Assert.Equal(0.632, Motion.Approach(0, 1, dt: 0.2, timeConstant: 0.2), 3);
    }

    [Fact]
    public void Approach_EdgeCases()
    {
        Assert.Equal(0.3, Motion.Approach(0.3, 1, dt: 0, timeConstant: 0.2));
        Assert.Equal(1, Motion.Approach(0, 1, dt: 10, timeConstant: 0.2), 6);
        Assert.Equal(1, Motion.Approach(0, 1, dt: 0.05, timeConstant: 0));
        Assert.Equal(0.3, Motion.Approach(0.3, 1, dt: -1, timeConstant: 0.2));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0.5, 1)]
    [InlineData(-0.2, 0)]
    [InlineData(1.3, 0)]
    public void Bell_RisesAndFalls(double p, double expected)
    {
        Assert.Equal(expected, Motion.Bell(p), 6);
    }

    [Fact]
    public void OutBack_OvershootsThenSettles()
    {
        Assert.Equal(0, Motion.OutBack(0), 6);
        Assert.Equal(1, Motion.OutBack(1), 6);
        Assert.True(Motion.OutBack(0.7) > 1);
        Assert.Equal(1, Motion.OutBack(2), 6);
    }

    [Fact]
    public void Approach_IsFrameRateIndependent()
    {
        var oneStep = Motion.Approach(0, 1, 0.1, 0.2);
        var twoSteps = Motion.Approach(Motion.Approach(0, 1, 0.05, 0.2), 1, 0.05, 0.2);

        Assert.Equal(oneStep, twoSteps, 9);
    }

    [Fact]
    public void NonFiniteInputs_NeverReturnNaN()
    {
        // Review (anim) #8: a single NaN would poison the breathing and flapping state for good.
        Assert.Equal(0.3, Motion.Approach(0.3, 1, double.NaN, 0.2));
        Assert.Equal(1, Motion.Approach(0.3, 1, 0.1, double.NaN));
        Assert.Equal(0.3, Motion.Approach(0.3, double.NaN, 0.1, 0.2));
        Assert.Equal(0, Motion.Bell(double.NaN));
        Assert.Equal(0, Motion.OutBack(double.NaN));
        Assert.Equal(0, Motion.Window(double.NaN, 0.2, 0.6));
        Assert.Equal(1, Motion.Window(0.7, 0.6, 0.6));
    }

    [Theory]
    [InlineData(0.1, 0)]
    [InlineData(0.2, 0)]
    [InlineData(0.4, 0.5)]
    [InlineData(0.6, 1)]
    [InlineData(0.9, 1)]
    public void Window_MapsASegmentTo0To1(double p, double expected)
    {
        Assert.Equal(expected, Motion.Window(p, 0.2, 0.6), 6);
    }
}
