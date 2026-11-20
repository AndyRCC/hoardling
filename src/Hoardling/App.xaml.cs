using System.Globalization;
using System.IO;
using System.Windows;
using Hoardling.Core;
using Hoardling.Rendering;
using Hoardling.Services;
using Hoardling.Windows;

namespace Hoardling;

public partial class App : Application
{
    private const int HookPort = 47823;
    private static readonly string ClaudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private const string QuitEventName = @"Local\Hoardling.Quit";
    private const string SingleInstanceName = @"Local\Hoardling.SingleInstance";
    private Mutex? _singleInstance;
    private EventWaitHandle? _quitSignal;
    private RegisteredWaitHandle? _quitWait;
    private bool _hooksRemovedOnExit;
    private SettingsStore? _settingsStore;
    private PetSettings _settings = PetSettings.Default;
    private PetWindow? _pet;
    private CardWindow? _card;
    private TrayIcon? _tray;
    private HookServer? _hooks;
    private PetCoordinator? _coordinator;
    private CardModel? _latestCard;
    private bool _hovering;
    private string? _logPath;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        switch (e.Args)
        {
            case ["--probe-ag", ..]:
                Shutdown(await Diagnostics.ProbeAntigravityAsync());
                return;
            case ["--render-gestures", var gesturesPath, ..]:
                Shutdown(Diagnostics.RenderGestures(gesturesPath));
                return;
            case ["--render-transitions", var transitionsPath, ..]:
                Shutdown(Diagnostics.RenderTransitions(transitionsPath));
                return;
            case ["--render-states", var path, ..]:
                var t = e.Args.Length > 2 && double.TryParse(e.Args[2], CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.3;
                Shutdown(Diagnostics.RenderStates(path, t));
                return;
        }

        if (e.Args.Contains("--log"))
        {
            _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hoardling", "state.log");
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        }

        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        if (e.Args.Contains("--quit"))
        {
            _quitSignal.Set();
            Shutdown(0);
            return;
        }

        // Already running: leave before relaunching anything (the mutex below still settles a race).
        if (Mutex.TryOpenExisting(SingleInstanceName, out var running))
        {
            running.Dispose();
            Shutdown(0);
            return;
        }

        // Started by a terminal, an IDE or Claude Code? Move out of their job so closing them can't kill the dragon.
        if (JobEscape.TryRelaunchOutsideJob(e.Args) is { } how)
        {
            Log($"launcher: {JobEscape.DescribeOwnJob()}; relaunched outside it via {how}");
            Shutdown(0);
            return;
        }

        var mutex = new Mutex(initiallyOwned: true, SingleInstanceName, out var firstInstance);
        if (!firstInstance)
        {
            // Only the primary instance may touch hooks on exit.
            mutex.Dispose();
            Shutdown(0);
            return;
        }
        _singleInstance = mutex;
        Log($"start: {(e.Args.Contains(JobEscape.DetachedFlag) ? "relaunched outside the starter's job" : "started directly")}, {JobEscape.DescribeOwnJob()}");

        // A 170 px window is cheap to draw in software; skipping Direct3D avoids loading GPU drivers (~tens of MB).
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => Dispatcher.InvokeAsync(Quit), null, Timeout.Infinite, executeOnlyOnce: true);
        SessionEnding += (_, _) =>
        {
            if (_pet is not null)
            {
                _pet.AllowClose = true;
            }
            RemoveHooksOnExit();
        };
        // A monitor unplugged or a resolution change: put the dragon back on a screen.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        LegacyMigration.Run(SettingsStore.DefaultPath, Environment.ProcessPath);
        _settingsStore = new SettingsStore(SettingsStore.DefaultPath);
        _settings = _settingsStore.Load();

        _pet = new PetWindow();
        _card = new CardWindow();
        _coordinator = new PetCoordinator(Dispatcher, OnPublish, Path.Combine(ClaudeDir, ".credentials.json"));
        _coordinator.Warning = _settings.HooksDisabled ? null : InstallHooks();

        var coordinator = _coordinator;
        _hooks = new HookServer(HookPort, json =>
        {
            LogHook(json);
            coordinator.OnHookEvent(json);
        });
        _coordinator.HookServerOk = _hooks.TryStart();

        PlacePet();
        _pet.Moved += (left, top) => SaveSettings(_settings with { Left = left, Top = top });
        _pet.HoverStarted += ShowCard;
        _pet.HoverEnded += HideCard;
        _pet.HiddenByUser += () =>
        {
            HideCard();
            if (_tray is not null)
            {
                _tray.PetVisible = false;
            }
        };
        _pet.Show();

        _tray = new TrayIcon(_pet, DragonIcon.CreatePng(32), "Hoardling")
        {
            StartupChecked = StartupRegistry.IsEnabled(),
            HooksChecked = !_settings.HooksDisabled,
            PetVisible = true,
        };
        _tray.ToggleRequested += TogglePet;
        _tray.StartupToggled += SetStartup;
        _tray.HooksToggled += SetHooks;
        _tray.ExitRequested += Quit;

        _coordinator.Start();
    }

    private void Quit()
    {
        if (_pet is not null)
        {
            _pet.AllowClose = true;
        }
        Shutdown(0);
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            StartupRegistry.Set(enabled, Environment.ProcessPath ?? "Hoardling.exe");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Blocked by policy: keep the menu truthful instead of crashing.
            if (_tray is not null)
            {
                _tray.StartupChecked = !enabled;
            }
        }
    }

    private void OnPublish(PetVisual visual, CardModel card)
    {
        _latestCard = card;
        _pet?.Apply(visual);
        Log($"{visual.Palette} {visual.Pose} glasses={visual.Glasses} " +
            $"claude={(visual.ClaudeRingShown ? Percent(visual.ClaudeRing) : "hidden")} " +
            $"ag={(visual.AgRingShown ? Percent(visual.AgRing) : "hidden")} sweat={visual.Sweat} | {card.StatusLine}");
        if (_hovering && _card is not null)
        {
            if (_card.IsVisible)
            {
                _card.Update(card);
            }
            else if (_pet is { IsVisible: true })
            {
                ShowCard(); // the mouse was already on the dragon before the first data arrived
            }
        }
        if (_tray is not null)
        {
            // Like the rings: only apps that are open.
            var parts = new List<string> { "Hoardling" };
            if (visual.ClaudeRingShown)
            {
                parts.Add($"Claude {Percent(visual.ClaudeRing)}");
            }
            if (visual.AgRingShown)
            {
                parts.Add($"Antigravity {Percent(visual.AgRing)}");
            }
            _tray.Tooltip = string.Join(" · ", parts);
        }
    }

    /// <summary>Diagnostics only (--log): state transitions and timings, never tokens or conversation content.</summary>
    private void Log(string line)
    {
        if (_logPath is null)
        {
            return;
        }
        try
        {
            lock (_logGate) // hook events log from the server's threads, states from the UI thread
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never break the pet (e.g. the log file is locked by another copy).
        }
    }

    private readonly Lock _logGate = new();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(PlacePet);

    /// <summary>Diagnostics only (--log): which hook arrived, from which session (8 chars), for which tool.</summary>
    private void LogHook(string json)
    {
        if (_logPath is null)
        {
            return;
        }
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            string? Field(string name) => root?[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? s) ? s : null;
            var session = Field("session_id");
            Log($"hook: {Field("hook_event_name")} session={(session is { Length: > 8 } ? session[..8] : session)} " +
                $"tool={Field("tool_name")} type={Field("notification_type")} subagent={(Field("agent_id") is null ? "no" : "yes")}");
        }
        catch (System.Text.Json.JsonException)
        {
            Log("hook: (unreadable)");
        }
    }

    private static string Percent(double? value) => value is { } v ? $"{Math.Round(v)}%" : "–";

    private void ShowCard()
    {
        _hovering = true;
        if (_pet is null || _card is null || _latestCard is null)
        {
            return;
        }
        var bounds = _pet.Bounds;
        _card.ShowFor(bounds, _latestCard, WorkAreaFor(bounds));
    }

    private void HideCard()
    {
        _hovering = false;
        _card?.HideCard();
    }

    private void TogglePet()
    {
        if (_pet is null || _tray is null)
        {
            return;
        }
        if (_pet.IsVisible)
        {
            HideCard();
            _pet.Hide();
        }
        else
        {
            _pet.Show();
        }
        _tray.PetVisible = _pet.IsVisible;
    }

    private void SetHooks(bool enabled)
    {
        if (_coordinator is null)
        {
            return;
        }
        if (enabled)
        {
            _coordinator.Warning = InstallHooks();
        }
        else
        {
            var outcome = Safe(() => ClaudeSettingsFile.RemoveHooks(Path.Combine(ClaudeDir, "settings.json")));
            _coordinator.Warning = outcome == HookInstallOutcome.InvalidSettings ? "no pude modificar settings.json de Claude" : null;
        }
        SaveSettings(_settings with { HooksDisabled = !enabled });
    }

    /// <summary>No ~/.claude means no Claude Code on this PC: never create its settings for it.</summary>
    private static string? InstallHooks() =>
        Directory.Exists(ClaudeDir)
        && Safe(() => ClaudeSettingsFile.EnsureHooks(Path.Combine(ClaudeDir, "settings.json"))) == HookInstallOutcome.InvalidSettings
            ? "no pude modificar settings.json de Claude"
            : null;

    private static HookInstallOutcome Safe(Func<HookInstallOutcome> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HookInstallOutcome.InvalidSettings;
        }
    }

    private void PlacePet()
    {
        if (_pet is null)
        {
            return;
        }
        var size = DragonRenderer.CanvasSize;
        var areas = WorkAreas();
        var (x, y) = WindowPlacement.Resolve(_settings.Left, _settings.Top, size, size, areas, areas[0]);
        _pet.Left = x;
        _pet.Top = y;
    }

    /// <summary>Each monitor's real work area (no taskbars, no gaps between monitors), primary first.</summary>
    private static List<RectD> WorkAreas()
    {
        var areas = NativeMethods.MonitorWorkAreas().Select(a => new RectD(a.X, a.Y, a.W, a.H)).ToList();
        if (areas.Count == 0)
        {
            var area = SystemParameters.WorkArea;
            areas.Add(new RectD(area.Left, area.Top, area.Width, area.Height));
        }
        return areas;
    }

    /// <summary>The work area of the monitor that holds most of the pet, so the card stays on that monitor.</summary>
    private static RectD WorkAreaFor(RectD pet)
    {
        var areas = WorkAreas();
        return areas.MaxBy(pet.OverlapArea) is { } best && pet.OverlapArea(best) > 0 ? best : areas[0];
    }

    private void SaveSettings(PetSettings settings)
    {
        _settings = settings;
        _settingsStore?.Save(settings);
    }

    /// <summary>
    /// Hooks exist only while the pet runs: with the pet closed, Claude Code would log a
    /// "hook error" (connection refused) on every event. Claude Code reloads hooks live.
    /// </summary>
    private void RemoveHooksOnExit()
    {
        if (_hooksRemovedOnExit || _settings.HooksDisabled || _singleInstance is null || !Directory.Exists(ClaudeDir))
        {
            return;
        }
        _hooksRemovedOnExit = true;
        Safe(() => ClaudeSettingsFile.RemoveHooks(Path.Combine(ClaudeDir, "settings.json")));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        RemoveHooksOnExit();
        Log($"exit: hooks removed after {watch.ElapsedMilliseconds} ms");
        _tray?.Dispose();
        // Dispose on the thread pool: waiting on the UI thread for work that resumes on the UI thread deadlocks.
        var coordinator = _coordinator;
        var hooks = _hooks;
        Task.Run(async () =>
        {
            // Stop the event source first, then the consumer.
            if (hooks is not null)
            {
                await hooks.DisposeAsync();
            }
            if (coordinator is not null)
            {
                await coordinator.DisposeAsync();
            }
        }).Wait(TimeSpan.FromSeconds(3));
        Log($"exit: background work stopped after {watch.ElapsedMilliseconds} ms");
        _singleInstance?.Dispose();
        _quitWait?.Unregister(null);
        _quitSignal?.Dispose();
        base.OnExit(e);
    }
}
