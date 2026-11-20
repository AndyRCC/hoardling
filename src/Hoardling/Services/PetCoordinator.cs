using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Hoardling.Core;

namespace Hoardling.Services;

/// <summary>
/// Two background loops on the thread pool:
/// - the poll loop checks processes, Antigravity and Claude on their own schedules (network can be slow);
/// - the publish loop computes the dragon's look and card every second, or immediately when a Claude
///   hook arrives, and hands it to the UI only when something changed.
/// A slow network call therefore never delays a hook. Problems never break either loop.
/// </summary>
public sealed class PetCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan ProcessEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AgActivityEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AgQuotaEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CredentialsCheckEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PublishEvery = TimeSpan.FromSeconds(1);

    private readonly Dispatcher _ui;
    private readonly Action<PetVisual, CardModel> _publish;
    private readonly ClaudeActivityTracker _claudeActivity = new();
    private readonly AntigravityActivityTracker _agActivity = new();
    private readonly ClaudePollScheduler _claudeSchedule = new();
    private readonly CredentialsStore _credentials;
    private readonly HttpClient _http;
    private readonly ClaudeUsageClient _claudeUsage;
    private readonly AntigravityClient _ag;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _state = new();
    private int _stopSignals;
    private Task? _pollLoop;
    private Task? _publishLoop;
    private volatile bool _hookServerOk = true;
    private volatile string? _warning;

    // Shared state: written by the poll loop, read by the publish loop, always under _state.
    private bool _claudeOpen;
    private bool _agOpen;
    private ProviderLimits? _claudeLimits;
    private ProviderLimits? _agLimits;
    private ClaudeDataStatus _claudeStatus = ClaudeDataStatus.Loading;

    // Poll loop only.
    private DateTimeOffset _nextProcessCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAgActivity = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAgQuota = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCredentialsCheck = DateTimeOffset.MinValue;
    private DateTime? _credentialsStamp;

    // Publish loop only.
    private PetVisual? _lastVisual;
    private CardModel? _lastCard;

    public PetCoordinator(Dispatcher ui, Action<PetVisual, CardModel> publish, string credentialsPath)
    {
        _ui = ui;
        _publish = publish;
        _credentials = new CredentialsStore(credentialsPath);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Hoardling/1.0");
        _claudeUsage = new ClaudeUsageClient(_http, _credentials, () => DateTimeOffset.Now);
        _ag = new AntigravityClient(() => DateTimeOffset.Now);
    }

    public bool HookServerOk
    {
        get => _hookServerOk;
        set { _hookServerOk = value; Wake(); }
    }

    public string? Warning
    {
        get => _warning;
        set { _warning = value; Wake(); }
    }

    /// <summary>Called on the hook server's threads. Wakes the publisher so the dragon reacts at once.</summary>
    public void OnHookEvent(string json)
    {
        if (_claudeActivity.OnHookEvent(json, DateTimeOffset.Now) == HookEventResult.Stop)
        {
            Interlocked.Increment(ref _stopSignals);
        }
        Wake();
    }

    /// <summary>Runs on the thread pool: never on (or posting back to) the WPF UI thread.</summary>
    public void Start()
    {
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
        _publishLoop = Task.Run(() => PublishLoopAsync(_cts.Token));
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
            // Already signalled, or a last hook event arrived while shutting down.
        }
    }

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Publish(DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Hoardling publish failed: {ex.GetType().Name}");
            }
            try
            {
                await _wake.WaitAsync(PublishEvery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Hoardling poll failed: {ex.GetType().Name}");
            }
        }
        while (await WaitAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        bool agOpen, claudeOpen;
        lock (_state)
        {
            agOpen = _agOpen;
            claudeOpen = _claudeOpen;
        }

        if (now >= _nextProcessCheck)
        {
            _nextProcessCheck = now + ProcessEvery;
            var claudeNowOpen = ProcessCheck.AnyRunning("claude");
            // Nothing is fetched for a closed Claude (its ring is hidden): when it opens, ask again rather
            // than show numbers that may have been spent on another device meanwhile.
            var refetchClaude = claudeNowOpen && !claudeOpen && _claudeSchedule.OnAppOpened(now);
            // Not "any Antigravity.exe": its background sidecars (Antigravity.exe running main.mjs) outlive
            // the app. Its own language server lives exactly as long as the app and is what we read from;
            // the language server's name varies by version, and a custom install folder may not say
            // "antigravity", so a windowed Antigravity.exe (sidecars have no window) also counts.
            var agNowOpen = ProcessCheck.AnyRunningPrefix("language_server", @"\antigravity\")
                || ProcessCheck.AnyRunningWithWindow("Antigravity");
            if (agOpen && !agNowOpen)
            {
                _ag.Reset();
            }
            if (!agOpen && agNowOpen)
            {
                _nextAgActivity = now;
                _nextAgQuota = now;
            }
            lock (_state)
            {
                if (agOpen && !agNowOpen)
                {
                    _agActivity.MarkUnavailable(now);
                    _agLimits = null;
                }
                if (refetchClaude)
                {
                    _claudeLimits = null;
                    _claudeStatus = ClaudeDataStatus.Loading;
                }
                _claudeOpen = claudeNowOpen;
                _agOpen = agNowOpen;
            }
            claudeOpen = claudeNowOpen;
            agOpen = agNowOpen;
            Wake();
        }

        if (agOpen)
        {
            if (now >= _nextAgActivity)
            {
                _nextAgActivity = now + AgActivityEvery;
                if (await _ag.GetActivityAsync(ct).ConfigureAwait(false) is { } snapshot)
                {
                    lock (_state)
                    {
                        _agActivity.Update(snapshot, DateTimeOffset.Now);
                    }
                    Wake();
                }
            }
            if (now >= _nextAgQuota)
            {
                _nextAgQuota = now + AgQuotaEvery;
                if (await _ag.GetQuotaAsync(ct).ConfigureAwait(false) is { } quota)
                {
                    lock (_state)
                    {
                        _agLimits = quota;
                    }
                    Wake();
                }
            }
        }

        if (Interlocked.Exchange(ref _stopSignals, 0) > 0)
        {
            _claudeSchedule.OnStop(now);
        }
        if (now >= _nextCredentialsCheck)
        {
            _nextCredentialsCheck = now + CredentialsCheckEvery;
            var stamp = _credentials.LastWriteTimeUtc;
            if (stamp != _credentialsStamp)
            {
                _credentialsStamp = stamp;
                _claudeSchedule.OnCredentialsChanged(now);
            }
        }
        if (claudeOpen && _claudeSchedule.IsDue(now))
        {
            var result = await _claudeUsage.FetchAsync(ct).ConfigureAwait(false);
            _claudeSchedule.OnResult(result, DateTimeOffset.Now);
            lock (_state)
            {
                _claudeStatus = result.Status;
                if (result.Limits is not null)
                {
                    _claudeLimits = result.Limits;
                }
                else if (result.Status is ClaudeDataStatus.NeedsLogin or ClaudeDataStatus.NoCredentials)
                {
                    // Logged out: the old numbers are no longer true.
                    _claudeLimits = null;
                }
            }
            Wake();
        }
    }

    private void Publish(DateTimeOffset now)
    {
        PetInputs inputs;
        ClaudeDataStatus claudeStatus;
        lock (_state)
        {
            inputs = new PetInputs(
                _claudeOpen, _agOpen,
                _claudeActivity.GetActivity(now), _agActivity.Activity,
                _claudeActivity.LastFinishedAt, _agActivity.LastFinishedAt,
                _claudeLimits, _agLimits, now);
            claudeStatus = _claudeStatus;
        }
        var visual = PetStateMachine.Compute(inputs);
        var card = CardModelBuilder.Build(inputs, claudeStatus, _hookServerOk, _warning);
        if (visual == _lastVisual && card == _lastCard)
        {
            return;
        }
        _lastVisual = visual;
        _lastCard = card;
        _ui.InvokeAsync(() => _publish(visual, card));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var loop in new[] { _pollLoop, _publishLoop })
        {
            if (loop is null)
            {
                continue;
            }
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
        }
        _ag.Dispose();
        _http.Dispose();
        _wake.Dispose();
        _cts.Dispose();
    }
}
