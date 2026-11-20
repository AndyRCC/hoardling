using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class AntigravityActivityTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 2, 0, 0, TimeSpan.Zero);

    private static AgTrajectorySnapshot Snap(AgRunState state, DateTimeOffset? modified = null) =>
        new(state, modified, []);

    [Fact]
    public void Working_Then_Idle_SetsFinished()
    {
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.Working, T0), T0);
        Assert.Equal(AppActivity.Thinking, t.Activity);
        Assert.Null(t.LastFinishedAt);

        t.Update(Snap(AgRunState.Idle, T0), T0.AddSeconds(30));

        Assert.Equal(AppActivity.Idle, t.Activity);
        Assert.Equal(T0.AddSeconds(30), t.LastFinishedAt);
    }

    [Fact]
    public void NeedsYou_Passthrough()
    {
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.NeedsYou, T0), T0);

        Assert.Equal(AppActivity.NeedsYou, t.Activity);
    }

    [Fact]
    public void FirstSnapshot_IdleWithOldModification_Idle()
    {
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.Idle, T0.AddDays(-1)), T0);

        Assert.Equal(AppActivity.Idle, t.Activity);
    }

    [Fact]
    public void Fallback_ModifiedRecently_Working()
    {
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.Idle, T0), T0);

        t.Update(Snap(AgRunState.Idle, T0.AddSeconds(3)), T0.AddSeconds(5));

        Assert.Equal(AppActivity.Thinking, t.Activity);
    }

    [Fact]
    public void Fallback_ModifiedLongAgo_Idle()
    {
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.Idle, T0), T0);

        t.Update(Snap(AgRunState.Idle, T0.AddSeconds(3)), T0.AddSeconds(14));

        Assert.Equal(AppActivity.Idle, t.Activity);
    }

    [Fact]
    public void Fallback_StaysWorkingWhileStillWithin10s()
    {
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.Idle, T0), T0);
        t.Update(Snap(AgRunState.Idle, T0.AddSeconds(3)), T0.AddSeconds(4));

        t.Update(Snap(AgRunState.Idle, T0.AddSeconds(3)), T0.AddSeconds(7));

        Assert.Equal(AppActivity.Thinking, t.Activity);
    }

    [Fact]
    public void Fallback_FromNullBaselineToRecentModified_Working()
    {
        // Review finding 13: first conversation ever.
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.Idle, null), T0);

        t.Update(Snap(AgRunState.Idle, T0.AddSeconds(2)), T0.AddSeconds(3));

        Assert.Equal(AppActivity.Thinking, t.Activity);
    }

    [Fact]
    public void MarkUnavailable_Idle_NoFinished()
    {
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.Working, T0), T0);

        t.MarkUnavailable(T0.AddSeconds(3));

        Assert.Equal(AppActivity.Idle, t.Activity);
        Assert.Null(t.LastFinishedAt);
    }

    [Fact]
    public void AfterUnavailable_FirstSnapshotDoesNotTriggerFallback()
    {
        var t = new AntigravityActivityTracker();
        t.Update(Snap(AgRunState.Idle, T0), T0);
        t.MarkUnavailable(T0.AddSeconds(1));

        t.Update(Snap(AgRunState.Idle, T0.AddSeconds(2)), T0.AddSeconds(3));

        Assert.Equal(AppActivity.Idle, t.Activity);
    }
}
