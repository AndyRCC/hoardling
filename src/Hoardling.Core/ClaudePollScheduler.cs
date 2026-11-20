namespace Hoardling.Core;

/// <summary>Decides when to ask Anthropic for usage again. Not thread-safe: coordinator loop only.</summary>
public sealed class ClaudePollScheduler
{
    public static readonly TimeSpan Regular = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AfterStop = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ErrorRetry = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastFetch;
    private TimeSpan _backoff = TimeSpan.Zero;
    private bool _waitingForCredentials;

    public bool IsDue(DateTimeOffset now) => !_waitingForCredentials && now >= _next;

    public void OnResult(ClaudeUsageResult result, DateTimeOffset now)
    {
        _lastFetch = now;
        if (result.Status is ClaudeDataStatus.NeedsLogin or ClaudeDataStatus.NoCredentials)
        {
            _waitingForCredentials = true;
            _backoff = TimeSpan.Zero;
            return;
        }
        if (result.RateLimited)
        {
            _backoff = _backoff == TimeSpan.Zero ? Regular * 2 : Min(_backoff * 2, MaxBackoff);
            var wait = result.RetryAfter is { } after && after > _backoff ? after : _backoff;
            _next = now + wait;
            return;
        }
        _backoff = TimeSpan.Zero;
        _next = now + (result.Status == ClaudeDataStatus.Ok ? Regular : ErrorRetry);
    }

    /// <summary>Claude finished a reply: refresh soon so the ring reflects it, without hammering the API.</summary>
    public void OnStop(DateTimeOffset now)
    {
        if (_waitingForCredentials || _backoff > TimeSpan.Zero)
        {
            return;
        }
        var target = now + AfterStop;
        if (_lastFetch is { } last && target < last + MinGap)
        {
            target = last + MinGap;
        }
        if (target < _next)
        {
            _next = target;
        }
    }

    /// <summary>
    /// Claude was just opened. Nothing is fetched while it is closed (its numbers aren't shown), so ask
    /// now unless the last answer is less than <see cref="MinGap"/> old. Returns true when it scheduled
    /// a fetch, i.e. the numbers on hand should not be shown until it arrives. Never overrides a
    /// rate-limit backoff or a wait for login.
    /// </summary>
    public bool OnAppOpened(DateTimeOffset now)
    {
        if (_waitingForCredentials || _backoff > TimeSpan.Zero)
        {
            return false;
        }
        if (_lastFetch is { } last && now - last < MinGap)
        {
            return false;
        }
        _next = now;
        return true;
    }

    public void OnCredentialsChanged(DateTimeOffset now)
    {
        if (!_waitingForCredentials)
        {
            return;
        }
        _waitingForCredentials = false;
        _next = now;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
