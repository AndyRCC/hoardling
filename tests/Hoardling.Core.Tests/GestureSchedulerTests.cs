using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class GestureSchedulerTests
{
    /// <summary>Calls Update every 0.05 s and records each gesture that starts.</summary>
    private static List<(Gesture Kind, double Start)> Run(GestureScheduler s, PetPose pose, double from, double to)
    {
        var started = new List<(Gesture, double)>();
        var previous = Gesture.None;
        for (var t = from; t < to; t += 0.05)
        {
            var g = s.Update(pose, t);
            if (g.Kind != Gesture.None && (previous == Gesture.None || g.Progress == 0))
            {
                started.Add((g.Kind, t));
            }
            previous = g.Kind;
        }
        return started;
    }

    [Fact]
    public void FirstGesture_WaitsTheInitialDelay()
    {
        var s = new GestureScheduler(new Random(1));

        Assert.Equal(Gesture.None, s.Update(PetPose.Idle, 0).Kind);
        Assert.Equal(Gesture.None, s.Update(PetPose.Idle, 1.9).Kind);
        Assert.NotEqual(Gesture.None, s.Update(PetPose.Idle, 5.01).Kind);
    }

    [Fact]
    public void Progress_GoesFrom0To1_ThenEnds()
    {
        var s = new GestureScheduler(new Random(2));
        s.Update(PetPose.Sleeping, 0);
        var start = s.Update(PetPose.Sleeping, 5.01);
        var duration = GestureScheduler.DurationOf(start.Kind);

        var half = s.Update(PetPose.Sleeping, 5.01 + duration / 2);
        var after = s.Update(PetPose.Sleeping, 5.01 + duration + 0.01);

        Assert.Equal(0, start.Progress, 6);
        Assert.Equal((start.Kind, 0.5), (half.Kind, Math.Round(half.Progress, 6)));
        Assert.Equal(Gesture.None, after.Kind);
    }

    [Fact]
    public void BetweenGestures_WaitsAtLeastTheMinimumPause()
    {
        // Before the AFK threshold (after it, pauses are shorter on purpose: see Afk_MoreYawnsAndFire).
        var starts = Run(new GestureScheduler(new Random(3)), PetPose.Idle, 0, GestureScheduler.AfkAfter - 1);

        for (var i = 1; i < starts.Count; i++)
        {
            var previousEnd = starts[i - 1].Start + GestureScheduler.DurationOf(starts[i - 1].Kind);
            var pause = starts[i].Start - previousEnd;
            Assert.True(pause >= 6 - 0.06 && pause <= 14 + 0.11, $"pause {pause:0.00} s");
        }
        Assert.InRange(starts.Count, 6, 17); // ~120 s / (6..14 s pause + gesture)
    }

    [Fact]
    public void PoseChange_CancelsTheGesture()
    {
        var s = new GestureScheduler(new Random(4));
        s.Update(PetPose.Idle, 0);
        Assert.NotEqual(Gesture.None, s.Update(PetPose.Idle, 5.01).Kind);

        Assert.Equal(Gesture.None, s.Update(PetPose.Typing, 5.2).Kind);
    }

    [Theory]
    [InlineData(PetPose.Idle, new[] { Gesture.LookAround, Gesture.TailWag, Gesture.WingStretch, Gesture.Yawn, Gesture.FireBurp, Gesture.Hop })]
    [InlineData(PetPose.Sleeping, new[] { Gesture.SnoreBubble, Gesture.SmokePuff, Gesture.Mumble, Gesture.TailTwitch, Gesture.Stretch })]
    [InlineData(PetPose.Thinking, new[] { Gesture.LookUp, Gesture.TailWag })]
    [InlineData(PetPose.Typing, new[] { Gesture.AdjustGlasses, Gesture.TypingBurst })]
    [InlineData(PetPose.NeedsYou, new[] { Gesture.Hop })]
    public void OnlyGesturesThatFitThePose(PetPose pose, Gesture[] allowed)
    {
        var kinds = Run(new GestureScheduler(new Random(5)), pose, 0, 1200).Select(g => g.Kind).Distinct().ToList();

        Assert.NotEmpty(kinds);
        Assert.All(kinds, k => Assert.Contains(k, allowed));
    }

    [Fact]
    public void Happy_HasNoGestures()
    {
        Assert.Empty(Run(new GestureScheduler(new Random(6)), PetPose.Happy, 0, 120));
    }

    [Fact]
    public void Afk_MoreYawnsAndFire()
    {
        var s = new GestureScheduler(new Random(7));
        static double Share(List<(Gesture Kind, double Start)> list) =>
            (double)list.Count(g => g.Kind is Gesture.Yawn or Gesture.FireBurp) / list.Count;

        var early = Run(s, PetPose.Idle, 0, GestureScheduler.AfkAfter);
        var afk = Run(s, PetPose.Idle, GestureScheduler.AfkAfter, 3000);

        Assert.True(Share(early) <= 0.35, $"early share {Share(early):0.00}");
        Assert.True(Share(afk) >= 0.45, $"AFK share {Share(afk):0.00}");
        var afkPauses = afk.Zip(afk.Skip(1), (a, b) => b.Start - a.Start - GestureScheduler.DurationOf(a.Kind)).ToList();
        Assert.True(afkPauses.Min() >= 4 - 0.06 && afkPauses.Max() <= 9 + 0.11);
    }

    [Fact]
    public void AfterLongClockJump_WaitsFullPauseBeforeNextGesture()
    {
        // Review (anim) #3: the dragon was hidden or the PC slept in the middle of a gesture.
        var s = new GestureScheduler(new Random(8));
        s.Update(PetPose.Sleeping, 0);
        Assert.NotEqual(Gesture.None, s.Update(PetPose.Sleeping, 5.01).Kind);

        Assert.Equal(Gesture.None, s.Update(PetPose.Sleeping, 500).Kind);
        Assert.Equal(Gesture.None, s.Update(PetPose.Sleeping, 506.9).Kind);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteClock_IsIgnored(double now)
    {
        var s = new GestureScheduler(new Random(9));
        s.Update(PetPose.Idle, 0);

        Assert.Equal(Gesture.None, s.Update(PetPose.Idle, now).Kind);
        Assert.NotEqual(Gesture.None, s.Update(PetPose.Idle, 5.01).Kind);
    }

    [Fact]
    public void Progress_StaysWithin0And1_EvenIfClockGoesBack()
    {
        var s = new GestureScheduler(new Random(10));
        s.Update(PetPose.Idle, 0);
        s.Update(PetPose.Idle, 5.01);

        var g = s.Update(PetPose.Idle, 4.0);

        Assert.InRange(g.Progress, 0, 1);
    }

    [Theory]
    [InlineData(PetPose.Idle, 6)]
    [InlineData(PetPose.Sleeping, 5)]
    [InlineData(PetPose.Thinking, 2)]
    [InlineData(PetPose.Typing, 2)]
    public void AllAllowedGestures_AppearOverALongRun(PetPose pose, int expectedKinds)
    {
        var kinds = Run(new GestureScheduler(new Random(11)), pose, 0, 3000).Select(g => g.Kind).Distinct().Count();

        Assert.Equal(expectedKinds, kinds);
    }

    [Fact]
    public void ReturnToIdle_RestartsTheAfkTimer()
    {
        var s = new GestureScheduler(new Random(12));
        static double Share(List<(Gesture Kind, double Start)> list) =>
            list.Count == 0 ? 0 : (double)list.Count(g => g.Kind is Gesture.Yawn or Gesture.FireBurp) / list.Count;
        Run(s, PetPose.Idle, 0, 200);             // AFK reached
        Run(s, PetPose.Typing, 200, 210);         // working again

        var fresh = Run(s, PetPose.Idle, 210, 210 + GestureScheduler.AfkAfter - 1);

        Assert.True(Share(fresh) <= 0.4, $"share {Share(fresh):0.00}: AFK should restart");
    }

    [Fact]
    public void EveryGesture_HasADuration()
    {
        Assert.All(Enum.GetValues<Gesture>().Where(g => g != Gesture.None), g => Assert.True(GestureScheduler.DurationOf(g) > 0));
    }

    [Fact]
    public void SameSeed_SameSequence()
    {
        var a = Run(new GestureScheduler(new Random(42)), PetPose.Sleeping, 0, 300);
        var b = Run(new GestureScheduler(new Random(42)), PetPose.Sleeping, 0, 300);

        Assert.Equal(a, b);
    }
}
