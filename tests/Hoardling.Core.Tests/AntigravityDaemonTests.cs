using Hoardling.Core;

namespace Hoardling.Core.Tests;

/// <summary>
/// Since Antigravity 2.34 the language server no longer gets --csrf_token on its command line; it
/// writes ~/.gemini/antigravity/daemon/hub_&lt;port&gt;.json with its ports and token instead.
/// </summary>
public class AntigravityDaemonTests
{
    private const string Real =
        """{"pid":2800,"httpsPort":30287,"httpPort":30288,"lsVersion":"2.34.0","csrfToken":"test-token"}""";

    [Fact]
    public void DaemonFile_Parsed_HttpsPortFirst()
    {
        var info = AntigravityParser.ParseDaemonFile(Real);

        Assert.NotNull(info);
        Assert.Equal(2800, info.Pid);
        Assert.Equal("test-token", info.CsrfToken);
        Assert.Equal([30287, 30288], info.Ports);
    }

    [Fact]
    public void DaemonFile_OnlyOnePort_IsEnough()
    {
        var info = AntigravityParser.ParseDaemonFile("""{"pid":5,"httpPort":41000,"csrfToken":"t"}""");

        Assert.NotNull(info);
        Assert.Equal([41000], info.Ports);
    }

    [Fact]
    public void DaemonFile_SamePortTwice_ListedOnce()
    {
        var info = AntigravityParser.ParseDaemonFile("""{"pid":5,"httpsPort":41000,"httpPort":41000,"csrfToken":"t"}""");

        Assert.Equal([41000], info!.Ports);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"pid":2800,"httpsPort":30287,"httpPort":30288}""")]
    [InlineData("""{"pid":2800,"httpsPort":30287,"httpPort":30288,"csrfToken":""}""")]
    [InlineData("""{"pid":2800,"httpsPort":30287,"httpPort":30288,"csrfToken":42}""")]
    [InlineData("""{"httpsPort":30287,"httpPort":30288,"csrfToken":"t"}""")]
    [InlineData("""{"pid":0,"httpsPort":30287,"httpPort":30288,"csrfToken":"t"}""")]
    [InlineData("""{"pid":2800,"csrfToken":"t"}""")]
    [InlineData("""{"pid":2800,"httpsPort":0,"httpPort":70000,"csrfToken":"t"}""")]
    [InlineData("""{"pid":2.5,"httpsPort":30287,"csrfToken":"t"}""")]
    public void DaemonFile_Unusable_IsNull(string json)
    {
        Assert.Null(AntigravityParser.ParseDaemonFile(json));
    }

    [Fact]
    public void PickLive_SkipsDeadAndBrokenFiles_NewestFirst_OnePerPid()
    {
        var t0 = new DateTime(2026, 11, 20, 17, 0, 0, DateTimeKind.Utc);
        (string, DateTime)[] files =
        [
            ("""{"pid":11188,"httpsPort":59304,"httpPort":59305,"csrfToken":"old-dead"}""", t0),
            ("""{"pid":2800,"httpsPort":30287,"httpPort":30288,"csrfToken":"older-live"}""", t0.AddMinutes(1)),
            ("garbage", t0.AddMinutes(2)),
            ("""{"pid":2800,"httpsPort":30297,"httpPort":30298,"csrfToken":"newest-live"}""", t0.AddMinutes(3)),
            ("""{"pid":4000,"httpsPort":40001,"csrfToken":"other-live"}""", t0.AddMinutes(2)),
        ];
        int[] alive = [2800, 4000];

        var picked = AntigravityParser.PickLiveDaemons(files, alive.Contains);

        Assert.Equal(["newest-live", "other-live"], picked.Select(d => d.CsrfToken));
    }

    [Fact]
    public void PickLive_NothingAlive_IsEmpty()
    {
        (string, DateTime)[] files = [(Real, DateTime.UtcNow)];

        Assert.Empty(AntigravityParser.PickLiveDaemons(files, _ => false));
    }
}
