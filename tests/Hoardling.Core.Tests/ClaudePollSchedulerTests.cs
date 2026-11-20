using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class ClaudePollSchedulerTests
{
    private static readonly DateTimeOffset T0 = TestData.T0;
    private static readonly ClaudeUsageResult Ok = new(ClaudeDataStatus.Ok, null, null, false);
    private static readonly ClaudeUsageResult RateLimited = new(ClaudeDataStatus.Error, null, null, true);

    [Fact]
    public void FirstCheck_IsDue()
    {
        Assert.True(new ClaudePollScheduler().IsDue(T0));
    }

    [Fact]
    public void Scheduler_RegularEvery5Min()
    {
        var s = new ClaudePollScheduler();
        s.OnResult(Ok, T0);

        Assert.False(s.IsDue(T0.AddMinutes(5).AddSeconds(-1)));
        Assert.True(s.IsDue(T0.AddMinutes(5)));
    }

    [Fact]
    public void Scheduler_AfterStop20s_ButMinGap60s()
    {
        var s = new ClaudePollScheduler();
        s.OnResult(Ok, T0);

        s.OnStop(T0.AddSeconds(10));
        Assert.False(s.IsDue(T0.AddSeconds(59)));
        Assert.True(s.IsDue(T0.AddSeconds(60)));

        s.OnResult(Ok, T0.AddSeconds(60));
        s.OnStop(T0.AddMinutes(3));
        Assert.False(s.IsDue(T0.AddMinutes(3).AddSeconds(19)));
        Assert.True(s.IsDue(T0.AddMinutes(3).AddSeconds(20)));
    }

    [Fact]
    public void Scheduler_429_Doubles_CapsAt30Min()
    {
        var s = new ClaudePollScheduler();
        var now = T0;
        var waits = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            s.OnResult(RateLimited, now);
            var wait = 0;
            while (!s.IsDue(now.AddMinutes(wait)))
            {
                wait++;
            }
            waits.Add(wait);
            now = now.AddMinutes(wait);
        }

        Assert.Equal([10, 20, 30, 30], waits);
    }

    [Fact]
    public void Scheduler_RetryAfterLongerThanBackoff_Wins()
    {
        var s = new ClaudePollScheduler();

        s.OnResult(RateLimited with { RetryAfter = TimeSpan.FromMinutes(25) }, T0);

        Assert.False(s.IsDue(T0.AddMinutes(24)));
        Assert.True(s.IsDue(T0.AddMinutes(25)));
    }

    [Fact]
    public void Scheduler_StopDuringBackoff_DoesNotShortenIt()
    {
        var s = new ClaudePollScheduler();
        s.OnResult(RateLimited, T0);

        s.OnStop(T0.AddMinutes(1));

        Assert.False(s.IsDue(T0.AddMinutes(5)));
    }

    [Fact]
    public void Scheduler_OkAfter429_ResetsBackoff()
    {
        var s = new ClaudePollScheduler();
        s.OnResult(RateLimited, T0);
        s.OnResult(Ok, T0.AddMinutes(10));

        Assert.True(s.IsDue(T0.AddMinutes(15)));
    }

    [Fact]
    public void Scheduler_Error_RetriesIn2Min()
    {
        var s = new ClaudePollScheduler();

        s.OnResult(new ClaudeUsageResult(ClaudeDataStatus.Error, null, null, false), T0);

        Assert.False(s.IsDue(T0.AddMinutes(2).AddSeconds(-1)));
        Assert.True(s.IsDue(T0.AddMinutes(2)));
    }

    [Theory]
    [InlineData(ClaudeDataStatus.NeedsLogin)]
    [InlineData(ClaudeDataStatus.NoCredentials)]
    public void Scheduler_NeedsLogin_WaitsForCredentialsChange(ClaudeDataStatus status)
    {
        var s = new ClaudePollScheduler();
        s.OnResult(new ClaudeUsageResult(status, null, null, false), T0);

        Assert.False(s.IsDue(T0.AddHours(5)));
        s.OnStop(T0.AddHours(5));
        Assert.False(s.IsDue(T0.AddHours(6)));

        s.OnCredentialsChanged(T0.AddHours(6));
        Assert.True(s.IsDue(T0.AddHours(6)));
    }
}
