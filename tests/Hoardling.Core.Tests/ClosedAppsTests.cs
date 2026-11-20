using Hoardling.Core;
using static Hoardling.Core.Tests.TestData;

namespace Hoardling.Core.Tests;

/// <summary>
/// Only an app that is open on this PC shows its ring and its card section: while it is closed its
/// limits may be spent elsewhere (another device), so any number we still hold would be stale.
/// </summary>
public class ClosedAppsTests
{
    private static readonly ClaudeUsageResult Ok = new(ClaudeDataStatus.Ok, null, null, false);
    private static readonly ClaudeUsageResult RateLimited = new(ClaudeDataStatus.Error, null, null, true);
    private static readonly ClaudeUsageResult LoggedOut = new(ClaudeDataStatus.NeedsLogin, null, null, false);

    [Fact]
    public void BothOpen_ShowBothRings()
    {
        var v = PetStateMachine.Compute(Inputs(claudeLimits: Claude(38), agLimits: Ag(40)));

        Assert.True(v.ClaudeRingShown);
        Assert.True(v.AgRingShown);
        Assert.Equal((38.0, 40.0), (v.ClaudeRing, v.AgRing));
    }

    [Fact]
    public void ClaudeClosed_HidesItsRing_AndItsLimitsNoLongerCount()
    {
        var v = PetStateMachine.Compute(Inputs(claudeOpen: false, claudeLimits: Claude(90, 100), agLimits: Ag(10)));

        Assert.False(v.ClaudeRingShown);
        Assert.Null(v.ClaudeRing);
        Assert.True(v.AgRingShown);
        Assert.Equal(10, v.AgRing);
        Assert.False(v.Sweat);
        Assert.False(v.Exhausted);
    }

    [Fact]
    public void AgClosed_HidesItsRing()
    {
        var v = PetStateMachine.Compute(Inputs(agOpen: false, claudeLimits: Claude(38), agLimits: Ag(90)));

        Assert.True(v.ClaudeRingShown);
        Assert.False(v.AgRingShown);
        Assert.Null(v.AgRing);
        Assert.False(v.Sweat);
    }

    [Fact]
    public void NothingOpen_NoRingsAtAll()
    {
        var v = PetStateMachine.Compute(Inputs(claudeOpen: false, agOpen: false, claudeLimits: Claude(100), agLimits: Ag(100)));

        Assert.Equal(PetPose.Sleeping, v.Pose);
        Assert.False(v.ClaudeRingShown);
        Assert.False(v.AgRingShown);
        Assert.Null(v.ClaudeRing);
        Assert.Null(v.AgRing);
    }

    [Fact]
    public void OpenWithoutData_ShowsAnEmptyRing()
    {
        var v = PetStateMachine.Compute(Inputs(claudeLimits: null, agLimits: null));

        Assert.True(v.ClaudeRingShown);
        Assert.Null(v.ClaudeRing);
    }

    [Fact]
    public void Card_ClaudeClosed_HasNoClaudeSection()
    {
        var m = CardModelBuilder.Build(Inputs(claudeOpen: false, claudeLimits: Claude(38), agLimits: Ag(5)), ClaudeDataStatus.Ok, true);

        Assert.Equal(["Antigravity"], m.Sections.Select(s => s.Title));
        Assert.Equal("Claude: cerrado · Antigravity: en reposo", m.StatusLine);
    }

    [Fact]
    public void Card_AgClosed_HasNoAgSection()
    {
        var m = CardModelBuilder.Build(Inputs(agOpen: false, claudeLimits: Claude(38), agLimits: Ag(5)), ClaudeDataStatus.Ok, true);

        Assert.Equal(["Claude"], m.Sections.Select(s => s.Title));
    }

    [Fact]
    public void Card_NothingOpen_NoSections_AndAHint()
    {
        var m = CardModelBuilder.Build(Inputs(claudeOpen: false, agOpen: false, claudeLimits: Claude(38), agLimits: Ag(5)), ClaudeDataStatus.Ok, true);

        Assert.Empty(m.Sections);
        Assert.Equal("Claude: cerrado · Antigravity: cerrado", m.StatusLine);
        Assert.Equal(CardModelBuilder.NothingOpenHint, m.UpdatedLine);
    }

    [Fact]
    public void Card_UpdatedLine_IgnoresClosedClaude()
    {
        var m = CardModelBuilder.Build(
            Inputs(claudeOpen: false, claudeLimits: Claude(38, fetchedAt: T0.AddSeconds(-10)), agLimits: Ag(5, fetchedAt: T0.AddMinutes(-5))),
            ClaudeDataStatus.Ok, true);

        Assert.Equal("Actualizado hace 5 min", m.UpdatedLine);
    }

    [Fact]
    public void Scheduler_AppOpened_WithOldData_FetchesNow()
    {
        var s = new ClaudePollScheduler();
        s.OnResult(Ok, T0);

        Assert.True(s.OnAppOpened(T0.AddMinutes(2)));
        Assert.True(s.IsDue(T0.AddMinutes(2)));
    }

    [Fact]
    public void Scheduler_AppOpened_WithDataFromAMomentAgo_KeepsIt()
    {
        var s = new ClaudePollScheduler();
        s.OnResult(Ok, T0);

        Assert.False(s.OnAppOpened(T0.AddSeconds(30)));
        Assert.False(s.IsDue(T0.AddSeconds(30)));
    }

    [Fact]
    public void Scheduler_AppOpened_RespectsRateLimitAndLogin()
    {
        var limited = new ClaudePollScheduler();
        limited.OnResult(RateLimited, T0);
        Assert.False(limited.OnAppOpened(T0.AddMinutes(2)));
        Assert.False(limited.IsDue(T0.AddMinutes(2)));

        var loggedOut = new ClaudePollScheduler();
        loggedOut.OnResult(LoggedOut, T0);
        Assert.False(loggedOut.OnAppOpened(T0.AddMinutes(2)));
        Assert.False(loggedOut.IsDue(T0.AddMinutes(2)));
    }

    [Fact]
    public void Scheduler_AppOpened_BeforeAnyFetch_IsDue()
    {
        var s = new ClaudePollScheduler();

        Assert.True(s.OnAppOpened(T0));
        Assert.True(s.IsDue(T0));
    }
}
