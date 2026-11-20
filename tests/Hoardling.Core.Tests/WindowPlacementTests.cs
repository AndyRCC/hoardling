using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class WindowPlacementTests
{
    private static readonly RectD Primary = new(0, 0, 1920, 1032);   // 1080 minus a 48 px taskbar
    private static readonly RectD Second = new(1920, 0, 1280, 984);
    private static readonly RectD[] Both = [Primary, Second];
    private const double W = 170, H = 170;

    [Fact]
    public void Resolve_NoSaved_BottomRightOfPrimary()
    {
        Assert.Equal((1920 - 170 - 12, 1032 - 170 - 12), WindowPlacement.Resolve(null, null, W, H, Both, Primary));
    }

    [Theory]
    [InlineData(100, 200)]
    [InlineData(2500, 500)]
    public void Resolve_SavedOnScreen_Kept(double x, double y)
    {
        Assert.Equal((x, y), WindowPlacement.Resolve(x, y, W, H, Both, Primary));
    }

    [Fact]
    public void Resolve_SavedOffScreen_Reset()
    {
        // The second monitor was unplugged.
        Assert.Equal((1920 - 170 - 12, 1032 - 170 - 12), WindowPlacement.Resolve(2500, 500, W, H, [Primary], Primary));
    }

    [Fact]
    public void Resolve_MostlyVisible_Kept()
    {
        Assert.Equal((1800.0, 900.0), WindowPlacement.Resolve(1800, 900, W, H, [Primary], Primary));
    }

    [Fact]
    public void Resolve_PartiallyOff_Clamped()
    {
        // Only a 40×170 strip remains visible: pull it fully inside.
        Assert.Equal((1920 - 170, 300.0), WindowPlacement.Resolve(1880, 300, W, H, [Primary], Primary));
    }

    [Fact]
    public void Card_LeftIfFits()
    {
        var pet = new RectD(1738, 850, 170, 170);

        var (x, _) = WindowPlacement.CardPosition(pet, 260, 200, Primary);

        Assert.Equal(1738 - 260 - 8, x);
    }

    [Fact]
    public void Card_RightWhenNoRoomLeft()
    {
        var pet = new RectD(20, 100, 170, 170);

        var (x, y) = WindowPlacement.CardPosition(pet, 260, 200, Primary);

        Assert.Equal((20 + 170 + 8, 100.0), (x, y));
    }

    [Fact]
    public void Card_ClampedVertically()
    {
        var pet = new RectD(1738, 900, 170, 170);

        var (_, y) = WindowPlacement.CardPosition(pet, 260, 300, Primary);

        Assert.Equal(1032 - 300, y);
    }
}
