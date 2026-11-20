using System.Windows;
using System.Windows.Media;
using Hoardling.Core;
using static Hoardling.Rendering.DragonArt;

namespace Hoardling.Rendering;

/// <summary>
/// The dragon as a retained scene of <see cref="DrawingGroup"/>s built once. <see cref="Animate"/> moves it
/// every frame by changing opacities, transforms and colours only (no allocations):
/// - accessories, eyes and mouths fade/slide/pop between states instead of switching (spec 2026-11-19 §1);
/// - breathing, flapping and typing run on accumulated phases, so changing rhythm never jumps;
/// - a short squash-and-stretch reacts to every change of pose or colour;
/// - occasional gestures (yawn, fire burp, snore bubble…) come from <see cref="GestureScheduler"/> (§2).
/// <see cref="Render"/> only records the rings plus the scene, so it runs only when the rings change.
/// </summary>
internal sealed class DragonRenderer
{
    public const double CanvasSize = 170;
    public const double ColorBlendSeconds = 0.6;
    private const double DragonOffset = 25;
    private const double FadeTime = 0.18;
    private const double ReactionSeconds = 0.35;
    private static readonly Point Center = new(85, 85);
    private const double ClaudeRadius = 78;
    private const double AgRadius = 70;

    private static readonly Brush HitArea = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    private static readonly Geometry RightHalfClip = Frozen(new RectangleGeometry(RightHalf));

    // ---- Scene ----------------------------------------------------------------------------------
    private readonly DrawingGroup _scene = new();
    private readonly DrawingGroup _zees = new();

    private readonly LivePalette _left = new();
    private readonly LivePalette _right = new();
    private readonly ColorTransition _colors = new(2 * PaletteSize, ColorBlendSeconds);
    private readonly uint[] _targetColors = new uint[2 * PaletteSize];
    private readonly uint[] _shownColors = new uint[2 * PaletteSize];

    // Whole-dragon motion.
    private readonly ScaleTransform _squash = new(1, 1, FeetCenter.X, FeetCenter.Y);
    private readonly RotateTransform _tilt = new(0, FeetCenter.X, FeetCenter.Y);
    private readonly TranslateTransform _bob = new();
    private readonly RotateTransform _tail = new(0, TailPivot.X, TailPivot.Y);
    private readonly RotateTransform _wingLeft = new(0, WingLeftPivot.X, WingLeftPivot.Y);
    private readonly RotateTransform _wingRight = new(0, WingRightPivot.X, WingRightPivot.Y);

    // Face.
    // Each cheek puffs around its own centre (one shared transform pushed them off the face: review anim #7).
    private readonly ScaleTransform _cheekLeft = new(1, 1, 44, 61);
    private readonly ScaleTransform _cheekRight = new(1, 1, 76, 61);
    private readonly SolidColorBrush _sparkleBrush = new(((SolidColorBrush)SparkleFill).Color);
    private DrawingGroup _rightBody = new();
    private readonly DrawingGroup _eyesOpen = new(), _eyesClosed = new(), _eyesHappy = new(), _eyesTired = new();
    private readonly ScaleTransform _blink = new(1, 1, 60, 51);
    private readonly TranslateTransform _look = new();
    private readonly DrawingGroup _mouthSmile = new(), _mouthOpen = new(), _mouthHappy = new(), _mouthWavy = new(), _mouthYawn = new();
    private readonly ScaleTransform _mumble = new(1, 1, MouthCenter.X, MouthCenter.Y);
    private readonly ScaleTransform _yawnScale = new(1, 1, MouthCenter.X, MouthCenter.Y);

    // Accessories.
    private readonly DrawingGroup _glasses = new();
    private readonly TranslateTransform _glassesMove = new();
    private readonly DrawingGroup _arms = new();
    private readonly DrawingGroup _palm = new() { Opacity = 0 };
    private readonly MatrixTransform _armLeft = new();
    private readonly MatrixTransform _armRight = new();
    private readonly DrawingGroup _laptop = new();
    private readonly TranslateTransform _laptopMove = new();
    private readonly TranslateTransform _handLeft = new();
    private readonly TranslateTransform _handRight = new();
    private readonly DrawingGroup _think = new();
    private readonly ScaleTransform _thinkPop = new(1, 1, 95, 20);
    private readonly SolidColorBrush[] _dots = [Dot(), Dot(), Dot()];
    private readonly DrawingGroup _alert = new();
    private readonly ScaleTransform _alertPop = new(1, 1, AlertCenter.X, AlertCenter.Y);
    private readonly DrawingGroup _sweat = new();
    private readonly TranslateTransform _sweatMove = new();
    private readonly DrawingGroup _sparkles = new();

    // Gesture effects.
    private readonly DrawingGroup _flame = new();
    private readonly MatrixTransform _flameMove = new();
    private readonly DrawingGroup[] _smoke = [new(), new(), new()];
    private readonly MatrixTransform[] _smokeMove = [new(), new(), new()];
    private readonly DrawingGroup _bubble = new();
    private readonly MatrixTransform _bubbleMove = new();

    // Sleep "z z".
    private readonly TranslateTransform _zee1 = new();
    private readonly TranslateTransform _zee2 = new();
    private readonly Pen _zee1Pen = ZeeInk();
    private readonly Pen _zee2Pen = ZeeInk();

    // ---- Animation state ------------------------------------------------------------------------
    private readonly GestureScheduler _gestures;
    private bool _initialized;
    private double _lastT;
    private PetPalette _lastPalette;
    private PetPose _lastPose;
    private double _reactionStart = double.NegativeInfinity;
    private double _bobPhase, _bobPeriod = 2.4, _bobAmplitude = 3;
    private double _wingPhase, _wingAmplitude = 12;
    private double _handPhase;
    private double _wGlasses, _wLaptop, _wThink, _wAlert, _wZzz, _wSweat, _wSparkle, _wRaised;
    private double _wEyesOpen = 1, _wEyesClosed, _wEyesHappy, _wEyesTired;
    private double _wSmile = 1, _wMouthOpen, _wMouthHappy, _wMouthWavy;
    private double _wClaudeRing, _wAgRing;
    private double? _claudeRingValue, _agRingValue; // last value while shown, kept while fading out
    private bool _ringsFading;
    private bool _moving;
    private GestureState _gesture;      // what is shown (frozen at its last progress while fading out)
    private double _gestureWeight;      // 1 while a gesture runs; fades to 0 if it is cut short

    public DragonRenderer(Random? random = null)
    {
        _gestures = new GestureScheduler(random ?? new Random());
        BuildScene();
    }

    /// <summary>True while a ring fades in or out: the rings are drawn immediately, so the view must redraw them.</summary>
    public bool RingsFading => _ringsFading;

    /// <summary>True while something is in motion beyond the idle loop (the view then runs at 20 fps).</summary>
    public bool NeedsFastFrames(double t) =>
        _moving || _colors.IsAnimating(t) || _gestureWeight > 0 || t - _reactionStart < ReactionSeconds;

    public void Render(DrawingContext dc, PetVisual v, bool withRings = true)
    {
        if (withRings)
        {
            // Nearly invisible disc: makes the whole round pet hoverable/draggable, gaps included.
            dc.DrawEllipse(HitArea, null, Center, ClaudeRadius + 3, ClaudeRadius + 3);
            DrawRing(dc, _wClaudeRing, ClaudeRadius, RingClaude, _claudeRingValue);
            DrawRing(dc, _wAgRing, AgRadius, RingAg, _agRingValue);
        }
        dc.DrawDrawing(_scene);
    }

    /// <summary>Advances the animation to time <paramref name="t"/> (seconds). <paramref name="forcedGesture"/> is for previews.</summary>
    public void Animate(PetVisual v, double t, GestureState? forcedGesture = null)
    {
        var dt = _initialized ? Math.Clamp(t - _lastT, 0, 0.25) : 0;
        if (_initialized && (v.Palette != _lastPalette || v.Pose != _lastPose))
        {
            _reactionStart = t;
        }
        _lastT = t;
        _lastPalette = v.Palette;
        _lastPose = v.Pose;
        // Fades advance at most one fast-frame step at a time: the first frame after a change at a slow
        // tick (4–10 fps) must not jump 40–75 % of the way (review anim #1).
        var fadeDt = Math.Min(dt, MaxFadeStep);
        UpdateGesture(forcedGesture ?? _gestures.Update(v.Pose, t), fadeDt);

        AnimateColors(v, t);
        AnimateWeights(v, fadeDt);
        AnimateBody(v, t, dt);
        AnimateFace(t);
        AnimateAccessories(t, dt);
        AnimateEffects();
        _initialized = true;
    }

    // ---- Per-frame -------------------------------------------------------------------------------

    private const double MaxFadeStep = 1.0 / 20;

    /// <summary>
    /// A finished gesture already ends at rest; one cut short by a state change is frozen where it was
    /// and faded out, so nothing it moved (mouth, eyes, tail, flame…) disappears in one frame.
    /// </summary>
    private void UpdateGesture(GestureState next, double dt)
    {
        if (next.Kind != Gesture.None)
        {
            _gesture = next;
            _gestureWeight = 1;
            return;
        }
        _gestureWeight = _initialized ? Motion.Approach(_gestureWeight, 0, dt, 0.15) : 0;
        if (_gestureWeight < 0.004)
        {
            _gestureWeight = 0;
            _gesture = default;
        }
    }

    /// <summary>Strength of a gesture-driven effect: 0 unless that gesture is shown.</summary>
    private double W(Gesture kind) => _gesture.Kind == kind ? _gestureWeight : 0;

    private void AnimateColors(PetVisual v, double t)
    {
        var (left, right) = For(v.Palette);
        left.CopyTo(_targetColors, 0);
        right.CopyTo(_targetColors, PaletteSize);
        _colors.SetTarget(_targetColors, t);
        _colors.Sample(t, _shownColors);
        _left.Apply(_shownColors.AsSpan(0, PaletteSize));
        _right.Apply(_shownColors.AsSpan(PaletteSize));
        // Both halves identical (everything but the fusion): skip drawing the clipped copy.
        var same = _shownColors.AsSpan(0, PaletteSize).SequenceEqual(_shownColors.AsSpan(PaletteSize));
        Op(_rightBody, same ? 0 : 1);
    }

    private void AnimateWeights(PetVisual v, double dt)
    {
        _moving = false;
        var pose = v.Pose;
        Fade(ref _wGlasses, v.Glasses, dt);
        Fade(ref _wLaptop, pose == PetPose.Typing, dt);
        Fade(ref _wThink, pose == PetPose.Thinking, dt);
        Fade(ref _wAlert, pose == PetPose.NeedsYou, dt);
        Fade(ref _wRaised, pose == PetPose.NeedsYou, dt);
        Fade(ref _wZzz, pose == PetPose.Sleeping, dt);
        Fade(ref _wSweat, v.Sweat && pose != PetPose.Sleeping, dt);
        Fade(ref _wSparkle, v.Palette == PetPalette.Fusion, dt);
        AnimateRings(v, dt);

        var sleeping = pose == PetPose.Sleeping;
        var happy = pose == PetPose.Happy;
        var tired = v.Exhausted && !sleeping && !happy;
        var needs = pose == PetPose.NeedsYou;
        Fade(ref _wEyesClosed, sleeping, dt);
        Fade(ref _wEyesHappy, happy, dt);
        Fade(ref _wEyesTired, tired, dt);
        Fade(ref _wEyesOpen, !sleeping && !happy && !tired, dt);
        Fade(ref _wMouthOpen, needs, dt);
        Fade(ref _wMouthHappy, happy, dt);
        Fade(ref _wMouthWavy, tired && !needs, dt);
        Fade(ref _wSmile, !happy && !needs && !tired, dt);
    }

    /// <summary>Only an open app has a ring; it fades in and out instead of popping.</summary>
    private void AnimateRings(PetVisual v, double dt)
    {
        var (claudeBefore, agBefore) = (_wClaudeRing, _wAgRing);
        if (v.ClaudeRingShown)
        {
            _claudeRingValue = v.ClaudeRing;
        }
        if (v.AgRingShown)
        {
            _agRingValue = v.AgRing;
        }
        Fade(ref _wClaudeRing, v.ClaudeRingShown, dt);
        Fade(ref _wAgRing, v.AgRingShown, dt);
        _ringsFading = _wClaudeRing != claudeBefore || _wAgRing != agBefore;
    }

    private void Fade(ref double weight, bool on, double dt)
    {
        var target = on ? 1.0 : 0.0;
        weight = _initialized ? Motion.Approach(weight, target, dt, FadeTime) : target;
        if (Math.Abs(weight - target) > 0.004)
        {
            _moving = true;
        }
        else
        {
            weight = target;
        }
    }

    private void AnimateBody(PetVisual v, double t, double dt)
    {
        var (period, amplitude) = v.Pose switch
        {
            PetPose.Sleeping => (4.5, 2.0),
            PetPose.Happy => (0.5, 7.0),
            PetPose.Thinking or PetPose.Typing or PetPose.NeedsYou => (1.1, 3.0),
            _ => (2.4, 3.0),
        };
        _bobPeriod = Motion.Approach(_bobPeriod, period, dt, 0.4);
        _bobAmplitude = Motion.Approach(_bobAmplitude, amplitude, dt, 0.3);
        _bobPhase = Frac(_bobPhase + dt / _bobPeriod);
        var (g, p) = (_gesture.Kind, _gesture.Progress);

        // Hop: crouch (anticipation), jump, then squash once back on the ground (review anim #9).
        var hopW = W(Gesture.Hop);
        var crouch = hopW * Motion.Bell(Motion.Window(p, 0, 0.16));
        var jump = hopW * 9 * Motion.Bell(Motion.Window(p, 0.14, 0.78));
        var landing = hopW * Motion.Bell(Motion.Window(p, 0.76, 1));
        Y(_bob, -_bobAmplitude * Wave01(_bobPhase) - jump);

        // Squash and stretch: reaction to a change, hop, sleepy stretch.
        var r = (t - _reactionStart) / ReactionSeconds;
        var react = r is >= 0 and < 1 ? Math.Sin(2 * Math.PI * r) * (1 - r) : 0;
        var stretch = W(Gesture.Stretch) * Motion.Bell(p);
        var squashY = 1 + 0.06 * react + 0.07 * stretch - 0.06 * crouch - 0.08 * landing;
        var squashX = 1 - 0.04 * react - 0.04 * stretch + 0.04 * crouch + 0.06 * landing;
        Scale(_squash, squashX, squashY);

        var plateau = Plateau(p);
        var mumble = W(Gesture.Mumble) * Math.Sin(2 * Math.PI * 4 * p) * Motion.Bell(p);
        Angle(_tilt, g switch
        {
            Gesture.Yawn => -4 * plateau * _gestureWeight,
            Gesture.LookUp => -3 * plateau * _gestureWeight,
            Gesture.Mumble => 1.5 * mumble,
            _ => 0,
        });

        Angle(_tail, g switch
        {
            Gesture.TailWag => 20 * Math.Sin(2 * Math.PI * 3 * p) * Motion.Bell(p) * _gestureWeight,
            Gesture.TailTwitch => 8 * Math.Sin(2 * Math.PI * 2 * p) * Motion.Bell(p) * _gestureWeight,
            _ => 0,
        });

        _wingAmplitude = Motion.Approach(_wingAmplitude, v.Pose == PetPose.Sleeping ? 0 : 12, dt, 0.3);
        _wingPhase = Frac(_wingPhase + dt / (v.Pose == PetPose.Typing ? 0.8 : 1.6));
        var flap = _wingAmplitude * Wave01(_wingPhase) + W(Gesture.WingStretch) * 24 * Motion.Bell(p);
        Angle(_wingLeft, -flap);
        Angle(_wingRight, flap);
    }

    private void AnimateFace(double t)
    {
        var (g, p) = (_gesture.Kind, _gesture.Progress);
        var plateau = Plateau(p);
        var yawn = W(Gesture.Yawn) * plateau;

        // Blink in every pose: the open eyes fade away by themselves when they should not show,
        // so a pose change in the middle of a blink never snaps the eyes open (review anim #6).
        Scale(_blink, 1, Math.Min(BlinkScale(t), 1 - 0.65 * yawn));
        X(_look, W(Gesture.LookAround) * LookAroundX(p));
        Y(_look, W(Gesture.LookUp) * -1.8 * plateau);

        // Fire burp: cheeks puff in place first, then the mouth opens as the flame comes out.
        var puff = 1 + 0.4 * W(Gesture.FireBurp) * Motion.Bell(Motion.Window(p, 0, 0.32));
        Scale(_cheekLeft, puff, puff);
        Scale(_cheekRight, puff, puff);
        var burpMouth = W(Gesture.FireBurp) * Motion.Bell(Motion.Window(p, 0.22, 0.55));
        var mumble = W(Gesture.Mumble) * Math.Sin(2 * Math.PI * 4 * p) * Motion.Bell(p);
        var otherMouths = (1 - yawn) * (1 - burpMouth);

        Op(_eyesOpen, _wEyesOpen);
        Op(_eyesClosed, _wEyesClosed);
        Op(_eyesHappy, _wEyesHappy);
        Op(_eyesTired, _wEyesTired);
        Op(_mouthSmile, _wSmile * otherMouths);
        Op(_mouthOpen, Math.Max(_wMouthOpen * (1 - yawn), Math.Max(burpMouth, 0.45 * Math.Abs(mumble))));
        Op(_mouthHappy, _wMouthHappy * otherMouths);
        Op(_mouthWavy, _wMouthWavy * otherMouths);
        Op(_mouthYawn, yawn);
        Scale(_yawnScale, 1, 0.4 + 0.6 * yawn);
        Scale(_mumble, 1 + 0.2 * mumble, 1 - 0.5 * mumble);
    }

    private void AnimateAccessories(double t, double dt)
    {
        var (g, p) = (_gesture.Kind, _gesture.Progress);

        Op(_glasses, _wGlasses);
        var lookUp = W(Gesture.LookUp) * Plateau(p);
        Y(_glassesMove, -10 * (1 - _wGlasses) - W(Gesture.AdjustGlasses) * 2.5 * Motion.Bell(p) - 0.8 * lookUp);

        Op(_laptop, _wLaptop);
        Op(_arms, 1 - _wLaptop);
        // Groups that are not visible are not touched at all: no work and no change notifications (review anim #4).
        if (_wLaptop > 0)
        {
            Y(_laptopMove, 14 * (1 - _wLaptop));
            _handPhase = Frac(_handPhase + dt / (g == Gesture.TypingBurst ? 0.16 : 0.32));
            Y(_handLeft, -2.5 * Wave01(_handPhase));
            Y(_handRight, -2.5 * Wave01(Frac(_handPhase + 0.5)));
        }

        // Right arm rises from its rest pose (pivoting at the shoulder) and waves while raised.
        var wave = 15 * Math.Sin(2 * Math.PI * t / 0.9) * _wRaised;
        SetArm(_armRight, ArmRightRest, ArmRightRaised, ArmRaisedAngle + wave, _wRaised);
        Op(_palm, _wRaised);

        Op(_think, _wThink);
        if (_wThink > 0)
        {
            var pop = Motion.OutBack(_wThink);
            Scale(_thinkPop, pop, pop);
            for (var i = 0; i < _dots.Length; i++)
            {
                _dots[i].Opacity = 0.2 + 0.8 * Wave01(Frac((t - 0.2 * i) / 1.2));
            }
        }

        Op(_alert, _wAlert);
        if (_wAlert > 0)
        {
            var pop = Motion.OutBack(_wAlert) * (1 + 0.08 * Wave01(Frac(t / 0.8)));
            Scale(_alertPop, pop, pop);
        }

        Op(_sweat, _wSweat);
        if (_wSweat > 0)
        {
            Y(_sweatMove, 2 * Wave01(Frac(t / 1.6)));
        }

        // Sparkles twinkle through their brush: a pulsing group opacity would force an offscreen layer.
        Op(_sparkles, _wSparkle);
        if (_wSparkle > 0)
        {
            _sparkleBrush.Opacity = 0.3 + 0.7 * Wave01(Frac(t / 1.0));
        }

        Op(_zees, _wZzz);
        if (_wZzz > 0)
        {
            AnimateZee(_zee1, _zee1Pen, Frac(t / 2.6));
            AnimateZee(_zee2, _zee2Pen, Frac((t + 1.3) / 2.6));
        }
    }

    private void AnimateEffects()
    {
        var (g, p) = (_gesture.Kind, _gesture.Progress);

        // Fire burp: a flame shoots out of the mouth sideways (away from the eyes), grows and fades into smoke.
        if (g == Gesture.FireBurp)
        {
            var flameP = Motion.Window(p, 0.25, 0.8);
            var flameGo = 1 - (1 - flameP) * (1 - flameP); // fast out, slowing down
            Op(_flame, W(Gesture.FireBurp) * Math.Min(1, 1.4 * Motion.Bell(flameP)));
            SetMove(_flameMove, 64 + 34 * flameGo, 66 - 12 * flameGo, 0.6 + 1.3 * flameGo, angle: 70 - 15 * flameGo);
        }
        else
        {
            Op(_flame, 0);
        }

        // Smoke: where the burp flame dies out, or from the nostrils while asleep (drifting to the sides).
        for (var i = 0; i < _smoke.Length; i++)
        {
            if (g == Gesture.FireBurp)
            {
                var q = Motion.Window(p, 0.62 + 0.08 * i, 1);
                Op(_smoke[i], W(Gesture.FireBurp) * 0.95 * Motion.Bell(q));
                SetMove(_smokeMove[i], 98 + 4 * i - 2 * q, 52 - 4 * i - 16 * q, 0.7 + 0.9 * q);
            }
            else if (g == Gesture.SmokePuff && i < 2)
            {
                var q = Motion.Window(p, 0.15 * i, 0.85 + 0.15 * i);
                Op(_smoke[i], W(Gesture.SmokePuff) * Motion.Bell(q));
                // Out of the nostrils, then away from the face to both sides and up, beside the head.
                var side = i == 0 ? -1 : 1;
                SetMove(_smokeMove[i], 60 + side * (3 + 30 * q), 59 - 24 * q, 0.6 + 0.9 * q);
            }
            else
            {
                Op(_smoke[i], 0);
            }
        }

        // Snore bubble: out of the right nostril; grows, shrinks a little, grows bigger and pops.
        if (g == Gesture.SnoreBubble)
        {
            var radius = 1.5 * SnoreRadius(p);
            Op(_bubble, W(Gesture.SnoreBubble) * (1 - Motion.Window(p, 0.9, 1)));
            SetMove(_bubbleMove, SnoreNostril.X + 0.75 * radius, SnoreNostril.Y + 0.25 * radius, Math.Max(radius, 0.01));
        }
        else
        {
            Op(_bubble, 0);
        }
    }

    // ---- Change-only setters: no boxing or change notification when the value is already there ----

    private static void Op(DrawingGroup group, double opacity)
    {
        if (group.Opacity != opacity)
        {
            group.Opacity = opacity;
        }
    }

    private static void X(TranslateTransform transform, double x)
    {
        if (transform.X != x)
        {
            transform.X = x;
        }
    }

    private static void Y(TranslateTransform transform, double y)
    {
        if (transform.Y != y)
        {
            transform.Y = y;
        }
    }

    private static void Angle(RotateTransform transform, double angle)
    {
        if (transform.Angle != angle)
        {
            transform.Angle = angle;
        }
    }

    private static void Scale(ScaleTransform transform, double x, double y)
    {
        if (transform.ScaleX != x)
        {
            transform.ScaleX = x;
        }
        if (transform.ScaleY != y)
        {
            transform.ScaleY = y;
        }
    }

    // ---- Gesture curves --------------------------------------------------------------------------

    /// <summary>Up quickly, hold, down quickly: for yawns and looks.</summary>
    private static double Plateau(double p) =>
        ColorTransition.Ease(Motion.Window(p, 0, 0.25)) * (1 - ColorTransition.Ease(Motion.Window(p, 0.7, 1)));

    private static double LookAroundX(double p)
    {
        var left = ColorTransition.Ease(Motion.Window(p, 0, 0.15));
        var across = ColorTransition.Ease(Motion.Window(p, 0.45, 0.6));
        var back = ColorTransition.Ease(Motion.Window(p, 0.85, 1));
        return -2.2 * left + 4.4 * across - 2.2 * back;
    }

    private static double SnoreRadius(double p)
    {
        if (p < 0.35)
        {
            return 5 * ColorTransition.Ease(p / 0.35);
        }
        if (p < 0.5)
        {
            return 5 - 2 * ColorTransition.Ease((p - 0.35) / 0.15);
        }
        if (p < 0.9)
        {
            return 3 + 4 * ColorTransition.Ease((p - 0.5) / 0.4);
        }
        return 7 + 3 * Motion.Window(p, 0.9, 1); // pop (fades out over the same last 10 %)
    }

    // ---- Scene construction (once) ----------------------------------------------------------------

    private void BuildScene()
    {
        var dragonMotion = new TransformGroup();
        dragonMotion.Children.Add(_squash);
        dragonMotion.Children.Add(_tilt);
        dragonMotion.Children.Add(_bob);

        var dragon = new DrawingGroup { Transform = dragonMotion };
        using (var dc = dragon.Open())
        {
            dc.DrawDrawing(Body(_left, clip: null));
            _rightBody = Body(_right, clip: RightHalfClip);
            dc.DrawDrawing(_rightBody);
            dc.DrawDrawing(Face());
            Fill(_glasses, null, dc2 =>
            {
                dc2.DrawGeometry(GlassesTint, GlassesPen, GlassesFrames);
                dc2.DrawGeometry(null, GlassesPen, GlassesBridge);
            }, _glassesMove);
            dc.DrawDrawing(_glasses);
            dc.DrawDrawing(Arms());
            dc.DrawDrawing(Laptop());
            dc.DrawDrawing(Overlays());
            dc.DrawDrawing(Effects());
        }

        var zeeGroup = _zees;
        using (var dc = zeeGroup.Open())
        {
            DrawZee(dc, _zee1, new TranslateTransform(88, 22), _zee1Pen);
            DrawZee(dc, _zee2, new MatrixTransform(0.8, 0, 0, 0.8, 99, 12), _zee2Pen);
        }

        _scene.Transform = Frozen(new TranslateTransform(DragonOffset, DragonOffset));
        using (var dc = _scene.Open())
        {
            dc.DrawDrawing(dragon);
            dc.DrawDrawing(_zees);
        }
    }

    private DrawingGroup Body(LivePalette p, Geometry? clip)
    {
        var tail = new DrawingGroup { Transform = _tail };
        using (var dc = tail.Open())
        {
            dc.DrawGeometry(null, p.Tail, TailCurve);
            dc.DrawGeometry(p.Spike, null, TailTip);
        }
        var body = new DrawingGroup { ClipGeometry = clip };
        using (var dc = body.Open())
        {
            dc.DrawDrawing(tail);
            DrawWith(dc, _wingLeft, d => d.DrawGeometry(p.Wing, null, WingLeft));
            DrawWith(dc, _wingRight, d => d.DrawGeometry(p.Wing, null, WingRight));
            dc.DrawGeometry(p.Body, null, Torso);
            dc.DrawGeometry(p.Belly, null, Belly);
            dc.DrawGeometry(p.Dark, null, FootLeft);
            dc.DrawGeometry(p.Dark, null, FootRight);
            dc.DrawGeometry(Horn, null, HornLeft);
            dc.DrawGeometry(Horn, null, HornRight);
            dc.DrawGeometry(p.Body, null, Head);
            dc.DrawGeometry(p.Spike, null, HeadSpike);
        }
        return body;
    }

    private DrawingGroup Face()
    {
        var eyeMotion = new TransformGroup();
        eyeMotion.Children.Add(_blink);
        eyeMotion.Children.Add(_look);
        Fill(_eyesOpen, null, dc =>
        {
            dc.DrawGeometry(Ink, null, EyeLeft);
            dc.DrawGeometry(White, null, ShineLeft);
            dc.DrawGeometry(Ink, null, EyeRight);
            dc.DrawGeometry(White, null, ShineRight);
        }, eyeMotion);
        Fill(_eyesClosed, null, dc => dc.DrawGeometry(null, InkPenThick, EyesClosed));
        Fill(_eyesHappy, null, dc => dc.DrawGeometry(null, InkPenThick, EyesHappy));
        Fill(_eyesTired, null, dc => dc.DrawGeometry(Ink, null, EyesTired));
        Fill(_mouthSmile, null, dc => dc.DrawGeometry(null, InkPen, Smile), _mumble);
        Fill(_mouthOpen, null, dc => dc.DrawGeometry(Ink, null, MouthOpen));
        Fill(_mouthHappy, null, dc => dc.DrawGeometry(Ink, null, MouthHappy));
        Fill(_mouthWavy, null, dc => dc.DrawGeometry(null, InkPen, MouthWavy));
        Fill(_mouthYawn, null, dc =>
        {
            dc.DrawGeometry(Ink, null, MouthYawn);
            dc.DrawGeometry(TongueFill, null, YawnTongue);
        }, _yawnScale);

        var face = new DrawingGroup();
        using (var dc = face.Open())
        {
            DrawWith(dc, _cheekLeft, d => d.DrawGeometry(Cheek, null, CheekLeft));
            DrawWith(dc, _cheekRight, d => d.DrawGeometry(Cheek, null, CheekRight));
            foreach (var part in new[] { _eyesOpen, _eyesClosed, _eyesHappy, _eyesTired })
            {
                dc.DrawDrawing(part);
            }
            dc.DrawGeometry(Ink, null, Nostrils);
            foreach (var part in new[] { _mouthSmile, _mouthOpen, _mouthHappy, _mouthWavy, _mouthYawn })
            {
                dc.DrawDrawing(part);
            }
        }
        return face;
    }

    private DrawingGroup Arms()
    {
        SetArm(_armLeft, ArmLeftRest, ArmLeftRest, 0, 0);
        SetArm(_armRight, ArmRightRest, ArmRightRaised, 0, 0);
        using var dc = _arms.Open();
        DrawWith(dc, _armLeft, d => d.DrawGeometry(_left.Dark, null, ArmShape));
        DrawWith(dc, _armRight, d =>
        {
            d.DrawGeometry(_right.Dark, null, ArmShape);
            // A light palm makes the raised, waving hand readable against the wing (same dark colour).
            Fill(_palm, null, p => p.DrawGeometry(_right.Belly, null, Palm));
            d.DrawDrawing(_palm);
        });
        return _arms;
    }

    private DrawingGroup Laptop()
    {
        Fill(_laptop, null, dc =>
        {
            dc.DrawGeometry(LaptopScreenBrush, null, LaptopScreen);
            dc.DrawGeometry(LaptopLogoBrush, null, LaptopLogo);
            dc.DrawGeometry(LaptopBaseBrush, null, LaptopBase);
            DrawWith(dc, _handLeft, d => d.DrawGeometry(_left.Dark, null, HandLeft));
            DrawWith(dc, _handRight, d => d.DrawGeometry(_right.Dark, null, HandRight));
        }, _laptopMove);
        return _laptop;
    }

    private DrawingGroup Overlays()
    {
        Fill(_think, null, dc =>
        {
            dc.DrawGeometry(White, BubblePen, ThinkSmall);
            dc.DrawGeometry(White, BubblePen, ThinkPill);
            for (var i = 0; i < ThinkDots.Length; i++)
            {
                dc.DrawGeometry(_dots[i], null, ThinkDots[i]);
            }
        }, _thinkPop);
        Fill(_alert, null, dc =>
        {
            dc.DrawGeometry(AlertFill, null, AlertBox);
            dc.DrawGeometry(AlertInk, null, AlertMark);
        }, _alertPop);
        Fill(_sweat, null, dc => dc.DrawGeometry(SweatFill, SweatPen, Sweat), _sweatMove);
        Fill(_sparkles, null, dc =>
        {
            foreach (var spot in SparkleSpots)
            {
                DrawWith(dc, new TranslateTransform(spot.X, spot.Y), d => d.DrawGeometry(_sparkleBrush, null, Sparkle));
            }
        });
        var overlays = new DrawingGroup();
        using (var dc = overlays.Open())
        {
            dc.DrawDrawing(_sweat);
            dc.DrawDrawing(_sparkles);
            dc.DrawDrawing(_think);
            dc.DrawDrawing(_alert);
        }
        return overlays;
    }

    private DrawingGroup Effects()
    {
        Fill(_flame, null, dc =>
        {
            dc.DrawGeometry(FlameOuterFill, null, FlameOuter);
            dc.DrawGeometry(FlameInnerFill, null, FlameInner);
        }, _flameMove);
        for (var i = 0; i < _smoke.Length; i++)
        {
            Fill(_smoke[i], null, dc => dc.DrawGeometry(SmokeFill, SmokePen, Puff), _smokeMove[i]);
        }
        Fill(_bubble, null, dc =>
        {
            dc.DrawGeometry(BubbleFill, BubblePenThin, BubbleShape);
            dc.DrawGeometry(White, null, BubbleShine);
        }, _bubbleMove);
        var effects = new DrawingGroup();
        using (var dc = effects.Open())
        {
            foreach (var smoke in _smoke)
            {
                dc.DrawDrawing(smoke);
            }
            dc.DrawDrawing(_flame);
            dc.DrawDrawing(_bubble);
        }
        _flame.Opacity = 0;
        _bubble.Opacity = 0;
        foreach (var smoke in _smoke)
        {
            smoke.Opacity = 0;
        }
        return effects;
    }

    private static void Fill(DrawingGroup group, Geometry? clip, Action<DrawingContext> draw, Transform? transform = null)
    {
        group.ClipGeometry = clip;
        group.Transform = transform;
        using var dc = group.Open();
        draw(dc);
    }

    private static void DrawWith(DrawingContext dc, Transform transform, Action<DrawingContext> draw)
    {
        dc.PushTransform(transform);
        draw(dc);
        dc.Pop();
    }

    // ---- Small helpers -------------------------------------------------------------------------

    private static void SetArm(MatrixTransform arm, Point rest, Point raised, double angle, double weight)
    {
        var m = Matrix.Identity;
        m.RotateAt(angle * weight, 0, 4); // pivot near the shoulder end of the arm, not its middle
        m.Translate(rest.X + (raised.X - rest.X) * weight, rest.Y + (raised.Y - rest.Y) * weight);
        if (arm.Matrix != m)
        {
            arm.Matrix = m;
        }
    }

    private static void SetMove(MatrixTransform move, double x, double y, double scale, double angle = 0)
    {
        var m = new Matrix(scale, 0, 0, scale, 0, 0);
        if (angle != 0)
        {
            m.Rotate(angle);
        }
        m.Translate(x, y);
        if (move.Matrix != m)
        {
            move.Matrix = m;
        }
    }

    private static void DrawRing(DrawingContext dc, double weight, double radius, Pen pen, double? percent)
    {
        if (weight <= 0)
        {
            return;
        }
        var fading = weight < 1;
        if (fading)
        {
            dc.PushOpacity(weight);
        }
        dc.DrawEllipse(null, RingTrack, Center, radius, radius);
        DrawArc(dc, pen, radius, percent);
        if (fading)
        {
            dc.Pop();
        }
    }

    private static void DrawArc(DrawingContext dc, Pen pen, double radius, double? percent)
    {
        if (percent is not { } p || p < 0.5)
        {
            return;
        }
        if (p >= 99.5)
        {
            dc.DrawEllipse(null, pen, Center, radius, radius);
            return;
        }
        var sweep = 360 * p / 100;
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(PointAt(radius, -90), isFilled: false, isClosed: false);
            ctx.ArcTo(PointAt(radius, -90 + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }

    private static Point PointAt(double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return new Point(Center.X + radius * Math.Cos(rad), Center.Y + radius * Math.Sin(rad));
    }

    private static void DrawZee(DrawingContext dc, Transform drift, Transform place, Pen pen)
    {
        dc.PushTransform(drift);
        dc.PushTransform(place);
        dc.DrawGeometry(null, pen, Zee);
        dc.Pop();
        dc.Pop();
    }

    private static void AnimateZee(TranslateTransform drift, Pen pen, double phase)
    {
        drift.X = 4 * phase;
        drift.Y = 4 - 10 * phase;
        pen.Brush.Opacity = Math.Sin(Math.PI * phase);
    }

    /// <summary>0 → 1 → 0 over one cycle of phase in [0, 1).</summary>
    private static double Wave01(double phase) => 0.5 - 0.5 * Math.Cos(2 * Math.PI * phase);

    private static double Frac(double x) => x - Math.Floor(x);

    private static double BlinkScale(double t)
    {
        var phase = Frac(t / 4.2);
        if (phase < 0.93)
        {
            return 1;
        }
        var k = (phase - 0.93) / 0.07;
        return 1 - 0.88 * (1 - Math.Abs(2 * k - 1));
    }

    private static SolidColorBrush Dot() => new(((SolidColorBrush)DotBrushTemplate).Color);

    private static Pen ZeeInk() =>
        new(new SolidColorBrush(((SolidColorBrush)ZeePen.Brush).Color), ZeePen.Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed class LivePalette
    {
        public readonly SolidColorBrush Body = new(), Dark = new(), Wing = new(), Belly = new(), Spike = new();
        public readonly Pen Tail;

        public LivePalette() => Tail = new Pen(Body, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        public void Apply(ReadOnlySpan<uint> colors)
        {
            Set(Body, colors[0]);
            Set(Dark, colors[1]);
            Set(Wing, colors[2]);
            Set(Belly, colors[3]);
            Set(Spike, colors[4]);
        }

        private static void Set(SolidColorBrush brush, uint argb)
        {
            var color = ToColor(argb);
            if (brush.Color != color)
            {
                brush.Color = color;
            }
        }
    }
}
