using Hoardling.Core;
using static Hoardling.Core.Tests.TestData;

namespace Hoardling.Core.Tests;

public class PetStateMachineTests
{
    [Fact]
    public void NothingOpen_Sleeping()
    {
        var v = PetStateMachine.Compute(Inputs(claudeOpen: false, agOpen: false,
            claudeLimits: Claude(100, 100), agLimits: Ag(100)));

        Assert.Equal(PetPalette.Sleepy, v.Palette);
        Assert.Equal(PetPose.Sleeping, v.Pose);
        Assert.False(v.Glasses);
        Assert.False(v.ClaudeRingShown || v.AgRingShown);
        Assert.False(v.Sweat);
        Assert.False(v.Exhausted);
    }

    [Fact]
    public void BothOpenIdle_Mint()
    {
        var v = PetStateMachine.Compute(Inputs());

        Assert.Equal((PetPalette.Mint, PetPose.Idle, false, true, true), (v.Palette, v.Pose, v.Glasses, v.ClaudeRingShown, v.AgRingShown));
    }

    [Theory]
    [InlineData(AppActivity.Typing, PetPose.Typing)]
    [InlineData(AppActivity.Thinking, PetPose.Thinking)]
    public void ClaudeWorking_OrangeWithGlasses(AppActivity activity, PetPose pose)
    {
        var v = PetStateMachine.Compute(Inputs(claude: activity));

        Assert.Equal((PetPalette.Claude, pose, true), (v.Palette, v.Pose, v.Glasses));
    }

    [Fact]
    public void AgWorking_BlueAlternates()
    {
        var at0 = PetStateMachine.Compute(Inputs(ag: AppActivity.Thinking, now: T0));
        var at5 = PetStateMachine.Compute(Inputs(ag: AppActivity.Thinking, now: T0.AddSeconds(5)));

        Assert.Equal((PetPalette.Antigravity, PetPose.Thinking, true), (at0.Palette, at0.Pose, at0.Glasses));
        Assert.Equal((PetPalette.Antigravity, PetPose.Typing, true), (at5.Palette, at5.Pose, at5.Glasses));
    }

    [Fact]
    public void BothWorking_FusionTyping()
    {
        var v = PetStateMachine.Compute(Inputs(claude: AppActivity.Thinking, ag: AppActivity.Thinking));

        Assert.Equal((PetPalette.Fusion, PetPose.Typing, true), (v.Palette, v.Pose, v.Glasses));
    }

    [Fact]
    public void NeedsYouBeatsWorking()
    {
        var v = PetStateMachine.Compute(Inputs(claude: AppActivity.NeedsYou, ag: AppActivity.Thinking));

        Assert.Equal((PetPalette.Claude, PetPose.NeedsYou, false), (v.Palette, v.Pose, v.Glasses));
    }

    [Fact]
    public void BothNeedYou_Fusion()
    {
        var v = PetStateMachine.Compute(Inputs(claude: AppActivity.NeedsYou, ag: AppActivity.NeedsYou));

        Assert.Equal((PetPalette.Fusion, PetPose.NeedsYou), (v.Palette, v.Pose));
    }

    [Fact]
    public void AgNeedsYou_Blue()
    {
        var v = PetStateMachine.Compute(Inputs(ag: AppActivity.NeedsYou));

        Assert.Equal((PetPalette.Antigravity, PetPose.NeedsYou), (v.Palette, v.Pose));
    }

    [Fact]
    public void Happy_For2Seconds()
    {
        var recent = PetStateMachine.Compute(Inputs(claudeFinishedAt: T0.AddSeconds(-1)));
        var old = PetStateMachine.Compute(Inputs(claudeFinishedAt: T0.AddSeconds(-3)));

        Assert.Equal((PetPalette.Claude, PetPose.Happy), (recent.Palette, recent.Pose));
        Assert.Equal((PetPalette.Mint, PetPose.Idle), (old.Palette, old.Pose));
    }

    [Fact]
    public void Happy_ClockSteppedBack_NotStuck()
    {
        // Review finding 16: the wall clock jumped back 5 minutes after Claude finished.
        var v = PetStateMachine.Compute(Inputs(claudeFinishedAt: T0.AddMinutes(5)));

        Assert.Equal(PetPose.Idle, v.Pose);
    }

    [Fact]
    public void BothHappy_Fusion()
    {
        var v = PetStateMachine.Compute(Inputs(claudeFinishedAt: T0.AddSeconds(-1), agFinishedAt: T0.AddSeconds(-1)));

        Assert.Equal((PetPalette.Fusion, PetPose.Happy), (v.Palette, v.Pose));
    }

    [Fact]
    public void WorkingBeatsHappy()
    {
        var v = PetStateMachine.Compute(Inputs(ag: AppActivity.Thinking, claudeFinishedAt: T0.AddSeconds(-1)));

        Assert.Equal(PetPalette.Antigravity, v.Palette);
    }

    [Fact]
    public void ClaudeClosed_IgnoresStaleActivity()
    {
        var v = PetStateMachine.Compute(Inputs(claudeOpen: false, claude: AppActivity.Typing, claudeFinishedAt: T0));

        Assert.Equal((PetPalette.Mint, PetPose.Idle), (v.Palette, v.Pose));
    }

    [Fact]
    public void Rings_ClaudeFiveHourAndMostUsedAgBucket()
    {
        var v = PetStateMachine.Compute(Inputs(claudeLimits: Claude(38, 90), agLimits: Ag(6, 40)));

        Assert.Equal(38, v.ClaudeRing);
        Assert.Equal(40, v.AgRing);
    }

    [Fact]
    public void AgClosed_AgRingNull()
    {
        var v = PetStateMachine.Compute(Inputs(agOpen: false, agLimits: Ag(40)));

        Assert.Null(v.AgRing);
    }

    [Fact]
    public void StaleLimits_RingNull()
    {
        var v = PetStateMachine.Compute(Inputs(claudeLimits: Claude(100, 100, fetchedAt: T0.AddMinutes(-16))));

        Assert.Null(v.ClaudeRing);
        Assert.False(v.Sweat);
        Assert.False(v.Exhausted);
    }

    [Fact]
    public void LimitsFetched15MinAgo_StillShown()
    {
        var v = PetStateMachine.Compute(Inputs(claudeLimits: Claude(50, fetchedAt: T0.AddMinutes(-15))));

        Assert.Equal(50, v.ClaudeRing);
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(79.9, false)]
    public void Sweat_At80(double used, bool sweat)
    {
        Assert.Equal(sweat, PetStateMachine.Compute(Inputs(agLimits: Ag(used))).Sweat);
    }

    [Fact]
    public void Exhausted_WhenWeeklyFull()
    {
        var v = PetStateMachine.Compute(Inputs(claudeLimits: Claude(10, 100)));

        Assert.True(v.Exhausted);
    }

    [Fact]
    public void Exhausted_IgnoresClosedAntigravity()
    {
        var v = PetStateMachine.Compute(Inputs(agOpen: false, agLimits: Ag(100)));

        Assert.False(v.Exhausted);
    }
}
