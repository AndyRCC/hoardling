namespace Hoardling.Core;

/// <summary>
/// Tracks Antigravity agent activity from periodic trajectory snapshots.
/// Working is reported as <see cref="AppActivity.Thinking"/>; the state machine animates it.
/// Not thread-safe: used only from the coordinator loop.
/// </summary>
public sealed class AntigravityActivityTracker
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(10);

    private bool _hasBaseline;
    private DateTimeOffset? _previousModified;
    private DateTimeOffset? _recentModified;

    public AppActivity Activity { get; private set; } = AppActivity.Idle;

    public DateTimeOffset? LastFinishedAt { get; private set; }

    public void Update(AgTrajectorySnapshot snapshot, DateTimeOffset now)
    {
        if (_hasBaseline && snapshot.LatestModified is { } modified && (_previousModified is null || modified > _previousModified))
        {
            _recentModified = modified;
        }
        _previousModified = snapshot.LatestModified;
        _hasBaseline = true;

        var next = snapshot.State switch
        {
            AgRunState.NeedsYou => AppActivity.NeedsYou,
            AgRunState.Working => AppActivity.Thinking,
            // Fallback when the status field says idle but conversations keep changing.
            _ when _recentModified is { } recent && now - recent <= RecentWindow => AppActivity.Thinking,
            _ => AppActivity.Idle,
        };

        if (Activity != AppActivity.Idle && next == AppActivity.Idle)
        {
            LastFinishedAt = now;
        }
        Activity = next;
    }

    public void MarkUnavailable(DateTimeOffset now)
    {
        Activity = AppActivity.Idle;
        _hasBaseline = false;
        _previousModified = null;
        _recentModified = null;
    }
}
