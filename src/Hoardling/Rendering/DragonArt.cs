using System.Windows;
using System.Windows.Media;
using Hoardling.Core;

namespace Hoardling.Rendering;

/// <summary>Frozen geometry and colours of the chibi dragon (approved sketch "B"), in a 120×120 space.</summary>
internal static class DragonArt
{
    /// <summary>Body, shade, wing, belly, spike — as ARGB so they can be blended over time.</summary>
    public const int PaletteSize = 5;

    public static readonly uint[] Mint = [0xFF8FD1A8, 0xFF74BC8F, 0xFF5FA87E, 0xFFE8F6E0, 0xFF4E9A6E];
    public static readonly uint[] Sleepy = [0xFFB7C4BC, 0xFFA2B2A8, 0xFF93A69A, 0xFFEEF2EC, 0xFF8A9C91];
    public static readonly uint[] Claude = [0xFFF28C52, 0xFFE57A45, 0xFFE06E3E, 0xFFFFE3C2, 0xFFD9653B];
    public static readonly uint[] Antigravity = [0xFF5B9BE6, 0xFF4A86D1, 0xFF3F74C2, 0xFFDDEBFB, 0xFF3566B0];

    /// <summary>Colours of the left and right halves (they differ only in the bicolour fusion).</summary>
    public static (uint[] Left, uint[] Right) For(PetPalette p) => p switch
    {
        PetPalette.Sleepy => (Sleepy, Sleepy),
        PetPalette.Claude => (Claude, Claude),
        PetPalette.Antigravity => (Antigravity, Antigravity),
        PetPalette.Fusion => (Claude, Antigravity),
        _ => (Mint, Mint),
    };

    public static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    // Body
    public static readonly Geometry TailCurve = G("M40 94 C22 98 12 84 20 70");
    public static readonly Geometry TailTip = G("M20 60 L28 72 L12 72 Z");
    public static readonly Geometry WingLeft = G("M42 70 L20 50 L26 63 L15 65 L34 80 Z");
    public static readonly Geometry WingRight = G("M78 70 L100 50 L94 63 L105 65 L86 80 Z");
    public static readonly Point WingLeftPivot = new(42, 74);
    public static readonly Point WingRightPivot = new(78, 74);
    public static readonly Geometry Torso = E(60, 86, 24, 22);
    public static readonly Geometry Belly = E(60, 91, 14, 14);
    public static readonly Geometry FootLeft = E(48, 106, 8, 5);
    public static readonly Geometry FootRight = E(72, 106, 8, 5);
    public static readonly Geometry HornLeft = G("M45 32 L41 15 L54 27 Z");
    public static readonly Geometry HornRight = G("M75 32 L79 15 L66 27 Z");
    public static readonly Geometry Head = E(60, 50, 27, 27);
    public static readonly Geometry HeadSpike = G("M55 25 L60 17 L65 25 Z");
    public static readonly Rect RightHalf = new(60, 0, 60, 120);

    // Face
    public static readonly Geometry CheekLeft = E(44, 61, 4.5, 2.6);
    public static readonly Geometry CheekRight = E(76, 61, 4.5, 2.6);
    public static readonly Geometry EyeLeft = E(50, 51, 5, 6.5);
    public static readonly Geometry EyeRight = E(70, 51, 5, 6.5);
    public static readonly Geometry ShineLeft = E(51.8, 48.6, 2, 2);
    public static readonly Geometry ShineRight = E(71.8, 48.6, 2, 2);
    public static readonly Point EyeLeftCenter = new(50, 51);
    public static readonly Point EyeRightCenter = new(70, 51);
    public static readonly Geometry EyesClosed = G("M45 52 Q50 56 55 52 M65 52 Q70 56 75 52");
    public static readonly Geometry EyesHappy = G("M45 53 Q50 46 55 53 M65 53 Q70 46 75 53");
    public static readonly Geometry EyesTired = G("M45 51 A5 4.5 0 0 0 55 51 Z M65 51 A5 4.5 0 0 0 75 51 Z");
    public static readonly Geometry Nostrils = Group(E(57, 58, 0.9, 0.9), E(63, 58, 0.9, 0.9));
    public static readonly Geometry Smile = G("M56 62 Q60 66 64 62");
    public static readonly Geometry MouthOpen = E(60, 63, 2.6, 3);
    public static readonly Geometry MouthHappy = G("M55 61 Q60 68 65 61 Z");
    public static readonly Geometry MouthWavy = G("M55 64 Q57.5 62 60 64 Q62.5 66 65 64");

    // Accessories
    public static readonly Geometry GlassesFrames = Group(E(50, 51, 8.5, 8.5), E(70, 51, 8.5, 8.5));
    public static readonly Geometry GlassesBridge = G("M58.5 50 Q60 48.5 61.5 50");
    public static readonly Geometry ArmLeft = E(44, 86, 4, 6);
    public static readonly Geometry ArmRight = E(76, 86, 4, 6);
    public static readonly Geometry ArmRaised = Rotated(E(85, 67, 4, 7.5), 35, 85, 67);
    public static readonly Geometry LaptopScreen = R(39, 73, 42, 25, 3);
    public static readonly Geometry LaptopLogo = E(60, 85.5, 3.2, 3.2);
    public static readonly Geometry LaptopBase = G("M33 104 L87 104 L82 98 L38 98 Z");
    public static readonly Geometry HandLeft = E(46, 99, 5, 3.5);
    public static readonly Geometry HandRight = E(74, 99, 5, 3.5);
    public static readonly Geometry ThinkSmall = E(86, 28, 2.5, 2.5);
    public static readonly Geometry ThinkPill = R(87, 5, 30, 18, 9);
    public static readonly Geometry[] ThinkDots = [E(95, 14, 2, 2), E(102, 14, 2, 2), E(109, 14, 2, 2)];
    public static readonly Geometry AlertBox = R(94, 3, 20, 22, 6);
    public static readonly Geometry AlertMark = Group(new RectangleGeometry(new Rect(102.4, 7, 3.2, 10), 1.5, 1.5), E(104, 20.6, 1.8, 1.8));
    public static readonly Point AlertCenter = new(104, 14);
    public static readonly Geometry Zee = G("M0 0 H6 L0 7 H6");
    public static readonly Geometry Sweat = G("M84 30 C86 34 88 37 86 40 C84 42 81 40 82 37 C82.5 35 83.5 33 84 30 Z");
    public static readonly Geometry Sparkle = G("M0 -6 L1.4 -1.4 L6 0 L1.4 1.4 L0 6 L-1.4 1.4 L-6 0 L-1.4 -1.4 Z");
    public static readonly Point[] SparkleSpots = [new(100, 30), new(16, 40), new(106, 88)];

    // Gestures
    public static readonly Geometry ArmShape = E(0, 0, 4, 6);
    public static readonly Point ArmLeftRest = new(44, 86);
    public static readonly Point ArmRightRest = new(76, 86);
    public static readonly Point ArmRightRaised = new(84, 61);
    public const double ArmRaisedAngle = 28;
    public static readonly Geometry Palm = E(0, -5, 2.7, 2.7);
    public static readonly Point TailPivot = new(40, 94);
    public static readonly Point FeetCenter = new(60, 111);
    public static readonly Geometry MouthYawn = E(60, 64.5, 3.4, 4.6);
    public static readonly Geometry YawnTongue = E(60, 67, 2.1, 1.4);
    public static readonly Point MouthCenter = new(60, 64);
    public static readonly Geometry FlameOuter = G("M0 -9 C4 -4 5 0 3 3 C1.5 5 -1.5 5 -3 3 C-5 0 -4 -4 0 -9 Z");
    public static readonly Geometry FlameInner = G("M0 -5 C2 -2 2.5 0 1.5 2 C0.7 3 -0.7 3 -1.5 2 C-2.5 0 -2 -2 0 -5 Z");
    public static readonly Geometry Puff = E(0, 0, 3, 3);
    public static readonly Geometry BubbleShape = E(0, 0, 1, 1);
    public static readonly Geometry BubbleShine = E(-0.35, -0.35, 0.22, 0.22);
    public static readonly Point SnoreNostril = new(63, 58);

    // Fixed colours
    public static readonly Brush Ink = B("#2A1A12");
    public static readonly Pen InkPen = P("#2A1A12", 1.6);
    public static readonly Pen InkPenThick = P("#2A1A12", 2.2);
    public static readonly Pen GlassesPen = P("#2A1A12", 2);
    public static readonly Brush GlassesTint = B("#2EFFFFFF");
    public static readonly Brush White = B("#FFFFFF");
    public static readonly Brush Horn = B("#FFF1D6");
    public static readonly Brush Cheek = B("#BFFF8E9A");
    public static readonly Brush LaptopScreenBrush = B("#3B4250");
    public static readonly Brush LaptopBaseBrush = B("#5A6273");
    public static readonly Brush LaptopLogoBrush = B("#D9FFFFFF");
    public static readonly Pen BubblePen = P("#BDBDBD", 1);
    public static readonly Brush DotBrushTemplate = B("#555555");
    public static readonly Brush AlertFill = B("#FFCF4A");
    public static readonly Brush AlertInk = B("#5A3D00");
    public static readonly Brush SweatFill = B("#7EC8F2");
    public static readonly Pen SweatPen = P("#4A9BD0", 0.8);
    public static readonly Brush SparkleFill = B("#FFC93C");
    public static readonly Pen ZeePen = P("#6B7C72", 1.8);
    public static readonly Brush FlameOuterFill = B("#FF8A2A");
    public static readonly Brush FlameInnerFill = B("#FFD84A");
    public static readonly Brush SmokeFill = B("#D9C6D2DC");
    public static readonly Pen SmokePen = P("#788694", 0.65);
    public static readonly Brush BubbleFill = B("#B3CFEFFF");
    public static readonly Pen BubblePenThin = P("#5FAEE0", 0.14);
    public static readonly Brush TongueFill = B("#FF8E9A");

    // Rings (drawn in the 170×170 canvas)
    public static readonly Pen RingTrack = P("#40808080", 6);
    public static readonly Pen RingClaude = Ring("#E8743B");
    public static readonly Pen RingAg = Ring("#378ADD");

    private static Pen Ring(string color)
    {
        var pen = new Pen(B(color), 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush B(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Pen P(string color, double thickness)
    {
        var pen = new Pen(B(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Geometry G(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static Geometry E(double cx, double cy, double rx, double ry)
    {
        var g = new EllipseGeometry(new Point(cx, cy), rx, ry);
        g.Freeze();
        return g;
    }

    private static Geometry R(double x, double y, double w, double h, double radius)
    {
        var g = new RectangleGeometry(new Rect(x, y, w, h), radius, radius);
        g.Freeze();
        return g;
    }

    private static Geometry Group(params Geometry[] parts)
    {
        var g = new GeometryGroup();
        foreach (var part in parts)
        {
            g.Children.Add(part);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Rotated(Geometry g, double angle, double cx, double cy)
    {
        var copy = g.Clone();
        copy.Transform = new RotateTransform(angle, cx, cy);
        copy.Freeze();
        return copy;
    }
}
