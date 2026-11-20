using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Hoardling.Core;

namespace Hoardling.Rendering;

/// <summary>Hosts the dragon. One timer, frame rate by pose, stopped while hidden.</summary>
public sealed class DragonView : FrameworkElement
{
    private readonly DragonRenderer _renderer = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PetVisual _visual = new(PetPalette.Sleepy, PetPose.Sleeping, false, false, false, null, null, false, false);

    public DragonView()
    {
        Width = DragonRenderer.CanvasSize;
        Height = DragonRenderer.CanvasSize;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = IntervalFor(_visual.Pose) };
        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    public PetVisual Visual
    {
        get => _visual;
        set
        {
            if (value == _visual)
            {
                return;
            }
            var old = _visual;
            _visual = value;
            var valuesChanged = value.ClaudeRing != old.ClaudeRing || value.AgRing != old.AgRing;
            var shownChanged = value.ClaudeRingShown != old.ClaudeRingShown || value.AgRingShown != old.AgRingShown;
            var lookChanged = value with
            {
                ClaudeRing = old.ClaudeRing, AgRing = old.AgRing,
                ClaudeRingShown = old.ClaudeRingShown, AgRingShown = old.AgRingShown,
            } != old;
            // Look changes and rings appearing or leaving animate: run at full rate for them right away.
            // A value-only ring update leaves the rate alone, so it never slows down a gesture (review anim #5).
            if (lookChanged || shownChanged)
            {
                SetInterval(BlendInterval);
            }
            // The scene is retained: only the rings need re-recording.
            if (valuesChanged || shownChanged)
            {
                InvalidateVisual();
            }
        }
    }

    private static readonly TimeSpan BlendInterval = TimeSpan.FromSeconds(1.0 / 20);

    private void Tick()
    {
        var t = _clock.Elapsed.TotalSeconds;
        _renderer.Animate(_visual, t);
        if (_renderer.RingsFading)
        {
            InvalidateVisual();
        }
        SetInterval(_renderer.NeedsFastFrames(t) ? BlendInterval : IntervalFor(_visual.Pose));
    }

    private void SetInterval(TimeSpan interval)
    {
        if (_timer.Interval != interval)
        {
            _timer.Interval = interval;
        }
    }

    public static int FpsFor(PetPose pose) => pose switch
    {
        PetPose.Sleeping => 4,
        PetPose.Idle => 10,
        _ => 20,
    };

    private static TimeSpan IntervalFor(PetPose pose) => TimeSpan.FromSeconds(1.0 / FpsFor(pose));

    protected override void OnRender(DrawingContext dc)
    {
        _renderer.Animate(_visual, _clock.Elapsed.TotalSeconds);
        _renderer.Render(dc, _visual);
    }

    private void UpdateTimer()
    {
        if (IsVisible)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }
}
