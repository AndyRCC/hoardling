using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class TimeFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(130 * 60, "2h 10m")]
    [InlineData(45 * 60, "45m")]
    [InlineData((3 * 24 + 4) * 3600, "3d 4h")]
    [InlineData(2 * 3600, "2h")]
    [InlineData(20, "<1m")]
    [InlineData(-30, "ya")]
    public void Until_FormatsRemaining(int seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Until(Now.AddSeconds(seconds), Now));
    }
}
