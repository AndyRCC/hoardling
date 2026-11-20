using System.IO;
using System.Runtime.InteropServices;
using Hoardling.Core;
using Hoardling.Services;

namespace Hoardling;

/// <summary>Command-line diagnostic modes. Output goes to the parent console; no secrets are printed.</summary>
internal static class Diagnostics
{
    public static async Task<int> ProbeAntigravityAsync()
    {
        AttachConsole(AttachParentProcess);
        var endpoints = LanguageServerLocator.FindAll();
        Console.WriteLine($"language servers: {endpoints.Count}");
        foreach (var e in endpoints)
        {
            Console.WriteLine($"  pid {e.Pid}, ports {string.Join(",", e.Ports)}");
        }
        using var client = new AntigravityClient(() => DateTimeOffset.Now);
        var quota = await client.GetQuotaAsync(CancellationToken.None);
        Console.WriteLine(quota is null ? "quota: none" : $"quota: plan {quota.PlanName}");
        foreach (var g in quota?.Groups ?? [])
        {
            foreach (var b in g.Buckets)
            {
                Console.WriteLine($"  {g.Name} · {b.Label}: {b.UsedPercent:0.#}% (resets {b.ResetsAt:u})");
            }
        }
        var activity = await client.GetActivityAsync(CancellationToken.None);
        Console.WriteLine(activity is null
            ? "activity: none"
            : $"activity: {activity.State}, latest {activity.LatestModified:u}, statuses [{string.Join(", ", activity.RawStatuses)}]");
        return quota is null ? 1 : 0;
    }

    /// <summary>Writes a contact sheet of representative states on light and dark backgrounds.</summary>
    public static int RenderStates(string path, double t)
    {
        // A null percent means that app is closed: no ring at all.
        static PetVisual V(PetPalette p, PetPose pose, bool glasses = false, bool sweat = false, bool exhausted = false,
            double? claude = 38, double? ag = 6) => new(p, pose, glasses, sweat, exhausted, claude, ag, claude is not null, ag is not null);
        PetVisual[] states =
        [
            V(PetPalette.Sleepy, PetPose.Sleeping, claude: null, ag: null),
            V(PetPalette.Mint, PetPose.Idle),
            V(PetPalette.Claude, PetPose.Thinking, glasses: true, ag: null),
            V(PetPalette.Claude, PetPose.Typing, glasses: true),
            V(PetPalette.Antigravity, PetPose.Thinking, glasses: true, claude: null),
            V(PetPalette.Fusion, PetPose.Typing, glasses: true, claude: 64, ag: 52),
            V(PetPalette.Claude, PetPose.NeedsYou),
            V(PetPalette.Claude, PetPose.Happy),
            V(PetPalette.Mint, PetPose.Idle, sweat: true, exhausted: true, claude: 100, ag: 85),
        ];
        const double cell = Rendering.DragonRenderer.CanvasSize;
        var sheet = new System.Windows.Media.DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            var light = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE));
            var dark = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1F, 0x22));
            dc.DrawRectangle(light, null, new System.Windows.Rect(0, 0, cell * states.Length, cell));
            dc.DrawRectangle(dark, null, new System.Windows.Rect(0, cell, cell * states.Length, cell));
            for (var row = 0; row < 2; row++)
            {
                for (var i = 0; i < states.Length; i++)
                {
                    var renderer = new Rendering.DragonRenderer();
                    dc.PushTransform(new System.Windows.Media.TranslateTransform(i * cell, row * cell));
                    renderer.Animate(states[i], t);
                    renderer.Render(dc, states[i]);
                    dc.Pop();
                }
            }
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(cell * states.Length * 2), (int)(cell * 2 * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(sheet);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = CreateWithParentDir(path);
        encoder.Save(file);
        return 0;
    }

    /// <summary>Frames of real colour blends: reposo → Claude, Claude → fusión, fusión → durmiendo.</summary>
    public static int RenderTransitions(string path)
    {
        static PetVisual V(PetPalette p, PetPose pose) => new(p, pose, false, false, false, 38, 6, true, true);
        (PetVisual From, PetVisual To)[] changes =
        [
            (V(PetPalette.Mint, PetPose.Idle), V(PetPalette.Claude, PetPose.Idle)),
            (V(PetPalette.Claude, PetPose.Idle), V(PetPalette.Fusion, PetPose.Idle)),
            (V(PetPalette.Fusion, PetPose.Idle), V(PetPalette.Sleepy, PetPose.Idle)),
        ];
        double[] offsets = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6];
        const double cell = Rendering.DragonRenderer.CanvasSize;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(cell * offsets.Length), (int)(cell * changes.Length), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.WhiteSmoke, null, new System.Windows.Rect(0, 0, cell * offsets.Length, cell * changes.Length));
        }
        bitmap.Render(background);
        for (var row = 0; row < changes.Length; row++)
        {
            var renderer = new Rendering.DragonRenderer();
            renderer.Animate(changes[row].From, 0);
            for (var col = 0; col < offsets.Length; col++)
            {
                // Brushes are live: render each frame right after animating it.
                renderer.Animate(changes[row].To, 1 + offsets[col]);
                var frame = new System.Windows.Media.DrawingVisual();
                using (var dc = frame.RenderOpen())
                {
                    dc.PushTransform(new System.Windows.Media.TranslateTransform(col * cell, row * cell));
                    renderer.Render(dc, changes[row].To);
                    dc.Pop();
                }
                bitmap.Render(frame);
            }
        }
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = CreateWithParentDir(path);
        encoder.Save(file);
        return 0;
    }

    /// <summary>One row per gesture (7 frames across its duration) plus rows for state transitions.</summary>
    public static int RenderGestures(string path)
    {
        static PetVisual V(PetPalette p, PetPose pose, bool glasses = false) => new(p, pose, glasses, false, false, 38, 6, true, true);
        var idle = V(PetPalette.Mint, PetPose.Idle);
        var sleep = V(PetPalette.Sleepy, PetPose.Sleeping) with { ClaudeRing = null, AgRing = null, ClaudeRingShown = false, AgRingShown = false };
        var think = V(PetPalette.Claude, PetPose.Thinking, true);
        var type = V(PetPalette.Claude, PetPose.Typing, true);
        var needs = V(PetPalette.Claude, PetPose.NeedsYou);
        (PetVisual Visual, Gesture Gesture)[] gestures =
        [
            (idle, Gesture.FireBurp), (idle, Gesture.Yawn), (idle, Gesture.LookAround), (idle, Gesture.TailWag),
            (idle, Gesture.WingStretch), (idle, Gesture.Hop), (sleep, Gesture.SnoreBubble), (sleep, Gesture.SmokePuff),
            (sleep, Gesture.Mumble), (sleep, Gesture.Stretch), (think, Gesture.LookUp), (type, Gesture.AdjustGlasses), (needs, Gesture.Hop),
        ];
        (PetVisual From, PetVisual To)[] transitions = [(idle, type), (type, needs), (idle, sleep), (think, idle)];
        const int frames = 7;
        const double cell = Rendering.DragonRenderer.CanvasSize;
        var rows = gestures.Length + transitions.Length;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(cell * frames), (int)(cell * rows), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.WhiteSmoke, null, new System.Windows.Rect(0, 0, cell * frames, cell * rows));
        }
        bitmap.Render(background);

        void Frame(Rendering.DragonRenderer renderer, PetVisual v, int row, int col)
        {
            var frame = new System.Windows.Media.DrawingVisual();
            using (var dc = frame.RenderOpen())
            {
                dc.PushTransform(new System.Windows.Media.TranslateTransform(col * cell, row * cell));
                renderer.Render(dc, v);
                dc.Pop();
            }
            bitmap.Render(frame);
        }

        for (var row = 0; row < gestures.Length; row++)
        {
            var (v, g) = gestures[row];
            var renderer = new Rendering.DragonRenderer(new Random(1));
            renderer.Animate(v, 0, default);
            var duration = GestureScheduler.DurationOf(g);
            for (var col = 0; col < frames; col++)
            {
                var p = col / (double)frames + 0.5 / frames;
                renderer.Animate(v, 1 + p * duration, new GestureState(g, p));
                Frame(renderer, v, row, col);
            }
        }
        for (var i = 0; i < transitions.Length; i++)
        {
            var (from, to) = transitions[i];
            var renderer = new Rendering.DragonRenderer(new Random(1));
            renderer.Animate(from, 0, default);
            for (var col = 0; col < frames; col++)
            {
                renderer.Animate(to, 0.08 * (col + 1), default);
                Frame(renderer, to, gestures.Length + i, col);
            }
        }

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = CreateWithParentDir(path);
        encoder.Save(file);
        return 0;
    }

    /// <summary>Render sheets go to e.g. out/states.png; out/ is gitignored, so it may not exist in a fresh clone.</summary>
    private static FileStream CreateWithParentDir(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }
        return File.Create(path);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
