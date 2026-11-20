using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class ClaudeUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_ReadsFiveHourAndWeek()
    {
        const string json = """
            {"five_hour":{"utilization":38.0,"resets_at":"2026-11-19T04:00:00Z"},
             "seven_day":{"utilization":21,"resets_at":null}}
            """;

        var limits = ClaudeUsageParser.Parse(json, "Pro", Now);

        var group = Assert.Single(limits.Groups);
        Assert.Equal("Claude", group.Name);
        Assert.Equal("Pro", limits.PlanName);
        Assert.Equal(Now, limits.FetchedAt);
        Assert.Collection(group.Buckets,
            b =>
            {
                Assert.Equal("Sesión 5 h", b.Label);
                Assert.Equal(LimitWindow.FiveHour, b.Window);
                Assert.Equal(38, b.UsedPercent);
                Assert.Equal(new DateTimeOffset(2026, 11, 19, 4, 0, 0, TimeSpan.Zero), b.ResetsAt);
            },
            b =>
            {
                Assert.Equal("Semana", b.Label);
                Assert.Equal(LimitWindow.Weekly, b.Window);
                Assert.Equal(21, b.UsedPercent);
                Assert.Null(b.ResetsAt);
            });
        Assert.Equal(38, limits.MaxUsed(LimitWindow.FiveHour));
    }

    [Fact]
    public void Parse_AddsModelWeeksWhenPresent()
    {
        const string json = """
            {"five_hour":{"utilization":1},"seven_day":{"utilization":2},
             "seven_day_opus":{"utilization":3},"seven_day_sonnet":{"utilization":4}}
            """;

        var labels = ClaudeUsageParser.Parse(json, null, Now).Groups[0].Buckets.Select(b => b.Label);

        Assert.Equal(["Sesión 5 h", "Semana", "Semana · Opus", "Semana · Sonnet"], labels);
    }

    [Fact]
    public void Parse_SkipsNullModelWeeks()
    {
        const string json = """
            {"five_hour":{"utilization":1},"seven_day":{"utilization":2},
             "seven_day_opus":null,"seven_day_sonnet":null}
            """;

        var labels = ClaudeUsageParser.Parse(json, null, Now).Groups[0].Buckets.Select(b => b.Label);

        Assert.Equal(["Sesión 5 h", "Semana"], labels);
    }

    [Theory]
    [InlineData(130, 100)]
    [InlineData(-5, 0)]
    public void Parse_ClampsUtilization(double raw, double expected)
    {
        var json = "{\"five_hour\":{\"utilization\":" + raw.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";

        var limits = ClaudeUsageParser.Parse(json, null, Now);

        Assert.Equal(expected, limits.MaxUsed(LimitWindow.FiveHour));
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    public void Parse_NonFiniteUtilization_Ignored(string raw)
    {
        // Review finding 10.
        var limits = ClaudeUsageParser.Parse("{\"five_hour\":{\"utilization\":" + raw + "},\"seven_day\":{\"utilization\":85}}", null, Now);

        Assert.Null(limits.MaxUsed(LimitWindow.FiveHour));
        Assert.Equal(85, limits.MaxUsed(LimitWindow.Weekly));
    }

    [Fact]
    public void Parse_AllNull_GivesEmptyBuckets()
    {
        var limits = ClaudeUsageParser.Parse("""{"five_hour":null,"seven_day":null}""", null, Now);

        Assert.Empty(limits.Groups[0].Buckets);
        Assert.Null(limits.MaxUsed(LimitWindow.FiveHour));
        Assert.False(limits.AnyExhausted);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Parse_Garbage_Throws(string json)
    {
        Assert.Throws<FormatException>(() => ClaudeUsageParser.Parse(json, null, Now));
    }

    [Theory]
    [InlineData(99.5, true)]
    [InlineData(99.4, false)]
    public void AnyExhausted_TrueAt99_5(double used, bool expected)
    {
        var json = "{\"five_hour\":{\"utilization\":10},\"seven_day\":{\"utilization\":" + used.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";

        Assert.Equal(expected, ClaudeUsageParser.Parse(json, null, Now).AnyExhausted);
    }

    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("max", "Max")]
    [InlineData("team", "Team")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void PlanFromSubscription(string? subscription, string? expected)
    {
        Assert.Equal(expected, ClaudeUsageParser.PlanFromSubscription(subscription));
    }
}
