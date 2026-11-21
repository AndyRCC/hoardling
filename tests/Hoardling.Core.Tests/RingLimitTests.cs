using Hoardling.Core;
using static Hoardling.Core.Tests.TestData;

namespace Hoardling.Core.Tests;

/// <summary>
/// A ring shows the most used of each model group's shortest limit. Groups with a 5-hour limit
/// count with it (the user's original rule); a group whose only limit is weekly, like Gemini 4
/// Argon on Antigravity's Pro plan, counts with that, so using it still moves the ring.
/// </summary>
public class RingLimitTests
{
    private static ProviderLimits AgWithArgon(double gemini5h, double argonWeekly, double geminiWeekly = 0) =>
        new("Pro",
            [
                new LimitGroup("Gemini",
                [
                    new LimitBucket("Semana", LimitWindow.Weekly, geminiWeekly, null),
                    new LimitBucket("5 h", LimitWindow.FiveHour, gemini5h, null),
                ]),
                new LimitGroup("Claude y GPT",
                [
                    new LimitBucket("Semana", LimitWindow.Weekly, 0, null),
                    new LimitBucket("5 h", LimitWindow.FiveHour, 0, null),
                ]),
                new LimitGroup("Gemini 4 Argon", [new LimitBucket("Semana", LimitWindow.Weekly, argonWeekly, null)]),
            ],
            T0);

    [Fact]
    public void GroupWithOnlyAWeeklyLimit_CountsWithIt()
    {
        Assert.Equal(31.7, AgWithArgon(gemini5h: 0.5, argonWeekly: 31.7).RingPercent());
    }

    [Fact]
    public void GroupWithAFiveHourLimit_IgnoresItsWeekly()
    {
        Assert.Equal(10, AgWithArgon(gemini5h: 10, argonWeekly: 2, geminiWeekly: 95).RingPercent());
    }

    [Fact]
    public void Claude_StillUsesItsFiveHourSession()
    {
        Assert.Equal(38, Claude(38, weekly: 90).RingPercent());
    }

    [Fact]
    public void GroupWithOnlyAnUnknownWindow_StillCounts()
    {
        var limits = new ProviderLimits(null,
            [new LimitGroup("X", [new LimitBucket("Diario", LimitWindow.Other, 12, null)])], T0);

        Assert.Equal(12, limits.RingPercent());
    }

    [Fact]
    public void NoBucketsAtAll_IsNull()
    {
        Assert.Null(new ProviderLimits(null, [], T0).RingPercent());
        Assert.Null(new ProviderLimits(null, [new LimitGroup("X", [])], T0).RingPercent());
    }

    [Fact]
    public void RealAntigravitySample_IncludesArgon()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "ag_quota_summary_argon.json"));

        var limits = AntigravityParser.ParseQuotaSummary(json, "Pro", T0);

        Assert.Equal(31.7, limits.RingPercent()!.Value, 0.05);
    }

    [Fact]
    public void UsingArgon_MovesTheBlueRing()
    {
        var v = PetStateMachine.Compute(Inputs(claudeLimits: Claude(20), agLimits: AgWithArgon(gemini5h: 0.5, argonWeekly: 31.7)));

        Assert.True(v.AgRingShown);
        Assert.Equal(31.7, v.AgRing);
        Assert.Equal(20, v.ClaudeRing);
    }

    [Fact]
    public void ArgonAlmostSpent_MakesTheDragonSweat()
    {
        var v = PetStateMachine.Compute(Inputs(claudeLimits: Claude(20), agLimits: AgWithArgon(gemini5h: 0.5, argonWeekly: 85)));

        Assert.True(v.Sweat);
        Assert.False(v.Exhausted);
    }
}
