namespace Hoardling.Core;

public enum Gesture
{
    None, LookAround, TailWag, WingStretch, Yawn, FireBurp, Hop,
    SnoreBubble, SmokePuff, Mumble, TailTwitch, Stretch,
    LookUp, AdjustGlasses, TypingBurst,
}

public readonly record struct GestureState(Gesture Kind, double Progress);

/// <summary>
/// Picks occasional "alive" gestures for the current pose, with random pauses in between.
/// A pose change cancels the gesture in progress. Idle for longer than <see cref="AfkAfter"/>
/// seconds counts as AFK: gestures come more often and lean to yawns and fire burps.
/// Time is in seconds from any monotonic clock. Not thread-safe (render thread only).
/// </summary>
public sealed class GestureScheduler(Random random)
{
    public const double AfkAfter = 120;
    private const double FirstDelayMin = 2, FirstDelayMax = 5;

    private sealed record Table((double Min, double Max) Pause, (Gesture Kind, double Weight)[] Gestures);

    private static readonly Table Idle = new((6, 14),
        [(Gesture.LookAround, 3), (Gesture.TailWag, 3), (Gesture.WingStretch, 2), (Gesture.Yawn, 1), (Gesture.FireBurp, 1), (Gesture.Hop, 2)]);
    private static readonly Table Afk = new((4, 9),
        [(Gesture.Yawn, 3), (Gesture.FireBurp, 3), (Gesture.LookAround, 1), (Gesture.TailWag, 1), (Gesture.WingStretch, 1), (Gesture.Hop, 1)]);
    private static readonly Table Sleeping = new((7, 15),
        [(Gesture.SnoreBubble, 3), (Gesture.SmokePuff, 2), (Gesture.Mumble, 2), (Gesture.TailTwitch, 2), (Gesture.Stretch, 1)]);
    private static readonly Table Thinking = new((5, 10), [(Gesture.LookUp, 3), (Gesture.TailWag, 1)]);
    private static readonly Table Typing = new((6, 12), [(Gesture.AdjustGlasses, 2), (Gesture.TypingBurst, 2)]);
    private static readonly Table NeedsYou = new((4, 7), [(Gesture.Hop, 1)]);

    private PetPose? _pose;
    private double _poseSince;
    private Gesture _current;
    private double _start;
    private double _end;
    private double _next;

    public static double DurationOf(Gesture gesture) => gesture switch
    {
        Gesture.LookAround => 2.0,
        Gesture.TailWag => 1.6,
        Gesture.WingStretch => 1.4,
        Gesture.Yawn => 2.2,
        Gesture.FireBurp => 2.0,
        Gesture.Hop => 0.8,
        Gesture.SnoreBubble => 3.2,
        Gesture.SmokePuff => 1.8,
        Gesture.Mumble => 1.2,
        Gesture.TailTwitch => 1.2,
        Gesture.Stretch => 1.8,
        Gesture.LookUp => 1.6,
        Gesture.AdjustGlasses => 1.0,
        Gesture.TypingBurst => 1.5,
        _ => 0,
    };

    public GestureState Update(PetPose pose, double now)
    {
        if (!double.IsFinite(now))
        {
            return default;
        }
        if (pose != _pose)
        {
            _pose = pose;
            _poseSince = now;
            _current = Gesture.None;
            _next = now + Uniform(FirstDelayMin, FirstDelayMax);
        }

        var table = TableFor(pose, now - _poseSince >= AfkAfter);
        if (_current != Gesture.None)
        {
            if (now < _end)
            {
                return new GestureState(_current, Math.Clamp((now - _start) / (_end - _start), 0, 1));
            }
            // Pause counted from now, not from the end: after a long hide or sleep the clock jumped,
            // and counting from the old end would chain the next gesture with no pause at all.
            _current = Gesture.None;
            if (table is not null)
            {
                _next = now + Uniform(table.Pause.Min, table.Pause.Max);
            }
            return default;
        }

        if (table is null || now < _next)
        {
            return default;
        }
        _current = Pick(table);
        _start = now;
        _end = now + DurationOf(_current);
        return new GestureState(_current, 0);
    }

    private static Table? TableFor(PetPose pose, bool afk) => pose switch
    {
        PetPose.Idle => afk ? Afk : Idle,
        PetPose.Sleeping => Sleeping,
        PetPose.Thinking => Thinking,
        PetPose.Typing => Typing,
        PetPose.NeedsYou => NeedsYou,
        _ => null,
    };

    private Gesture Pick(Table table)
    {
        var total = table.Gestures.Sum(g => g.Weight);
        var roll = random.NextDouble() * total;
        foreach (var (kind, weight) in table.Gestures)
        {
            roll -= weight;
            if (roll < 0)
            {
                return kind;
            }
        }
        return table.Gestures[^1].Kind;
    }

    private double Uniform(double min, double max) => min + random.NextDouble() * (max - min);
}
