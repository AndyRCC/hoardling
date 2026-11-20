using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class AntigravityParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 2, 0, 0, TimeSpan.Zero);

    private static string Sample(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    [Fact]
    public void QuotaSummary_RealSample()
    {
        var limits = AntigravityParser.ParseQuotaSummary(Sample("ag_quota_summary.json"), "Pro", Now);

        Assert.Equal(["Gemini", "Claude y GPT", "Gemini 4 Argon"], limits.Groups.Select(g => g.Name));
        Assert.Equal("Pro", limits.PlanName);
        var gemini5h = limits.Groups[0].Buckets.Single(b => b.Window == LimitWindow.FiveHour);
        Assert.Equal(5.5, gemini5h.UsedPercent, 0.01);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 4, 7, 27, TimeSpan.Zero), gemini5h.ResetsAt);
        Assert.Equal(5.5, limits.MaxUsed(LimitWindow.FiveHour)!.Value, 0.01);
    }

    [Fact]
    public void QuotaSummary_MissingRemainingFraction_Is100Used()
    {
        const string json = """
            {"response":{"groups":[{"displayName":"Gemini Models","buckets":[
              {"window":"5h","displayName":"Five Hour Limit Remaining","resetTime":"2026-11-19T04:00:00Z"}]}]}}
            """;

        var limits = AntigravityParser.ParseQuotaSummary(json, null, Now);

        Assert.Equal(100, limits.Groups[0].Buckets[0].UsedPercent);
        Assert.True(limits.AnyExhausted);
    }

    [Fact]
    public void QuotaSummary_WindowLabels()
    {
        const string json = """
            {"response":{"groups":[{"displayName":"X","buckets":[
              {"window":"5h","remainingFraction":1},
              {"window":"weekly","remainingFraction":0.5},
              {"window":"daily","displayName":"Daily Limit","remainingFraction":0.25}]}]}}
            """;

        var buckets = AntigravityParser.ParseQuotaSummary(json, null, Now).Groups[0].Buckets;

        Assert.Equal(("5 h", LimitWindow.FiveHour, 0.0), (buckets[0].Label, buckets[0].Window, buckets[0].UsedPercent));
        Assert.Equal(("Semana", LimitWindow.Weekly, 50.0), (buckets[1].Label, buckets[1].Window, buckets[1].UsedPercent));
        Assert.Equal(("Daily Limit", LimitWindow.Other, 75.0), (buckets[2].Label, buckets[2].Window, buckets[2].UsedPercent));
    }

    [Fact]
    public void QuotaSummary_UnknownGroupKeepsDisplayName()
    {
        const string json = """{"response":{"groups":[{"displayName":"Nuevo grupo","buckets":[]}]}}""";

        Assert.Equal("Nuevo grupo", AntigravityParser.ParseQuotaSummary(json, null, Now).Groups[0].Name);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("nope")]
    public void QuotaSummary_Garbage_Throws(string json)
    {
        Assert.Throws<FormatException>(() => AntigravityParser.ParseQuotaSummary(json, null, Now));
    }

    [Fact]
    public void PlanName_Read()
    {
        const string json = """{"userStatus":{"planStatus":{"planInfo":{"planName":"Pro"}}}}""";

        Assert.Equal("Pro", AntigravityParser.ParsePlanName(json));
    }

    [Theory]
    [InlineData("""{"userStatus":{}}""")]
    [InlineData("""{"userStatus":[]}""")]
    [InlineData("""{"userStatus":{"planStatus":"pro"}}""")]
    [InlineData("garbage")]
    public void PlanName_Missing_IsNull(string json)
    {
        Assert.Null(AntigravityParser.ParsePlanName(json));
    }

    [Fact]
    public void Trajectories_AllIdle()
    {
        const string json = """
            {"trajectorySummaries":{
              "a":{"status":"CASCADE_RUN_STATUS_IDLE","lastModifiedTime":"2026-11-14T03:38:29.394890800Z"},
              "b":{"status":"CASCADE_RUN_STATUS_IDLE","lastModifiedTime":"2026-11-19T00:16:26.199524800Z"}}}
            """;

        var snap = AntigravityParser.ParseTrajectories(json);

        Assert.Equal(AgRunState.Idle, snap.State);
        var expected = new DateTimeOffset(2026, 11, 19, 0, 16, 26, TimeSpan.Zero).AddTicks(1995248);
        Assert.Equal(expected, snap.LatestModified);
        Assert.Equal(["CASCADE_RUN_STATUS_IDLE", "CASCADE_RUN_STATUS_IDLE"], snap.RawStatuses);
    }

    [Fact]
    public void Trajectories_AnyRunning()
    {
        const string json = """
            {"trajectorySummaries":{
              "a":{"status":"CASCADE_RUN_STATUS_IDLE"},
              "b":{"status":"CASCADE_RUN_STATUS_RUNNING"}}}
            """;

        Assert.Equal(AgRunState.Working, AntigravityParser.ParseTrajectories(json).State);
    }

    [Fact]
    public void Trajectories_WaitingForUser()
    {
        const string json = """
            {"trajectorySummaries":{
              "a":{"status":"CASCADE_RUN_STATUS_RUNNING"},
              "b":{"status":"CASCADE_RUN_STATUS_WAITING_FOR_USER"}}}
            """;

        Assert.Equal(AgRunState.NeedsYou, AntigravityParser.ParseTrajectories(json).State);
    }

    [Fact]
    public void Trajectories_Empty()
    {
        var snap = AntigravityParser.ParseTrajectories("{}");

        Assert.Equal(AgRunState.Idle, snap.State);
        Assert.Null(snap.LatestModified);
        Assert.Empty(snap.RawStatuses);
    }

    [Theory]
    [InlineData("x.exe --csrf_token abc-123 --foo 1", "abc-123")]
    [InlineData("x.exe --csrf_token=abc-123 --foo 1", "abc-123")]
    [InlineData("x.exe --csrf_token \"abc-123\" --foo 1", "abc-123")]
    [InlineData("x.exe \"--csrf_token=abc-123\" --foo", "abc-123")]
    [InlineData("x.exe --csrf_token \"\" --foo", null)]
    [InlineData("x.exe --foo 1", null)]
    [InlineData("", null)]
    public void Csrf_Extracted(string commandLine, string? expected)
    {
        Assert.Equal(expected, AntigravityParser.ExtractCsrfToken(commandLine));
    }
}
