using Hoardling.Core;
using static Hoardling.Core.Tests.TestData;

namespace Hoardling.Core.Tests;

public class CardModelBuilderTests
{
    private static CardSection Section(CardModel m, string title) => m.Sections.Single(s => s.Title == title);

    [Fact]
    public void Card_ClaudeRowsWithReset()
    {
        var input = Inputs(claudeLimits: Claude(38, 21, resets: T0.AddMinutes(130)));

        var claude = Section(CardModelBuilder.Build(input, ClaudeDataStatus.Ok, true), "Claude");

        Assert.Equal("Pro", claude.Plan);
        Assert.Null(claude.Notice);
        Assert.Equal(new CardRow("Sesión 5 h", 38, "se reinicia en 2h 10m"), claude.Rows[0]);
        Assert.Equal(new CardRow("Semana", 21, null), claude.Rows[1]);
    }

    [Theory]
    [InlineData(ClaudeDataStatus.NeedsLogin)]
    [InlineData(ClaudeDataStatus.NoCredentials)]
    public void Card_NeedsLoginNotice(ClaudeDataStatus status)
    {
        var claude = Section(CardModelBuilder.Build(Inputs(), status, true), "Claude");

        Assert.Equal("Inicia sesión: abre una terminal, escribe claude y luego /login", claude.Notice);
        Assert.Empty(claude.Rows);
    }

    [Fact]
    public void Card_ClaudeErrorNotice()
    {
        var claude = Section(CardModelBuilder.Build(Inputs(), ClaudeDataStatus.Error, true), "Claude");

        Assert.Equal("No se pudo leer el uso de Claude", claude.Notice);
    }

    [Fact]
    public void Card_ClaudeLoading()
    {
        var claude = Section(CardModelBuilder.Build(Inputs(), ClaudeDataStatus.Loading, true), "Claude");

        Assert.Equal("Cargando…", claude.Notice);
    }

    [Fact]
    public void Card_StaleClaudeData_HiddenWithNotice()
    {
        var input = Inputs(claudeLimits: Claude(38, fetchedAt: T0.AddMinutes(-20)));

        var claude = Section(CardModelBuilder.Build(input, ClaudeDataStatus.Error, true), "Claude");

        Assert.Empty(claude.Rows);
        Assert.Equal("No se pudo leer el uso de Claude", claude.Notice);
    }

    [Fact]
    public void Card_AgRowsPrefixedByGroup()
    {
        var ag = Section(CardModelBuilder.Build(Inputs(agLimits: Ag(6, 0)), ClaudeDataStatus.Ok, true), "Antigravity");

        Assert.Equal(["Gemini · 5 h", "Gemini · Semana", "Claude y GPT · 5 h"], ag.Rows.Select(r => r.Label));
        Assert.Equal("Pro", ag.Plan);
    }

    [Fact]
    public void Card_StatusLine()
    {
        var m = CardModelBuilder.Build(Inputs(claude: AppActivity.Typing, agOpen: false), ClaudeDataStatus.Ok, true);

        Assert.Equal("Claude: trabajando · Antigravity: cerrado", m.StatusLine);
    }

    [Fact]
    public void Card_StatusLine_NeedsYouAndIdle()
    {
        var m = CardModelBuilder.Build(Inputs(claude: AppActivity.NeedsYou), ClaudeDataStatus.Ok, true);

        Assert.Equal("Claude: te necesita · Antigravity: en reposo", m.StatusLine);
    }

    [Fact]
    public void Card_HookServerDown()
    {
        var m = CardModelBuilder.Build(Inputs(), ClaudeDataStatus.Ok, false);

        Assert.EndsWith(" · sin actividad de Claude (puerto ocupado)", m.StatusLine);
    }

    [Fact]
    public void Card_ExtraWarningAppended()
    {
        var m = CardModelBuilder.Build(Inputs(), ClaudeDataStatus.Ok, true, "no pude leer settings.json de Claude");

        Assert.EndsWith(" · no pude leer settings.json de Claude", m.StatusLine);
    }

    [Fact]
    public void Card_UpdatedLine()
    {
        var m = CardModelBuilder.Build(Inputs(claudeLimits: Claude(1, fetchedAt: T0.AddSeconds(-70)), agLimits: Ag(1, fetchedAt: T0.AddMinutes(-5))),
            ClaudeDataStatus.Ok, true);

        Assert.Equal("Actualizado hace 1 min", m.UpdatedLine);
    }

    [Fact]
    public void Card_UpdatedJustNow()
    {
        var m = CardModelBuilder.Build(Inputs(claudeLimits: Claude(1, fetchedAt: T0.AddSeconds(-20))), ClaudeDataStatus.Ok, true);

        Assert.Equal("Actualizado hace un momento", m.UpdatedLine);
    }

    [Fact]
    public void Card_SameInputs_ValueEqual()
    {
        // Review finding 6: the coordinator only republishes when the card changes.
        var input = Inputs(claudeLimits: Claude(38, 21, resets: T0.AddMinutes(130)), agLimits: Ag(6, 2));

        var first = CardModelBuilder.Build(input, ClaudeDataStatus.Ok, true);
        var second = CardModelBuilder.Build(input, ClaudeDataStatus.Ok, true);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, CardModelBuilder.Build(input with { Claude = AppActivity.Typing }, ClaudeDataStatus.Ok, true));
    }

    [Fact]
    public void Card_ClaudeOkButStale_ShowsNotice()
    {
        // Review finding 11: after the PC wakes up, before the next fetch.
        var claude = Section(CardModelBuilder.Build(Inputs(claudeLimits: Claude(38, fetchedAt: T0.AddMinutes(-20))), ClaudeDataStatus.Ok, true), "Claude");

        Assert.Empty(claude.Rows);
        Assert.Equal("Sin datos recientes", claude.Notice);
    }

    [Fact]
    public void Card_AgClosedAndNoClaudeData_UpdatedLineSaysNoData()
    {
        var m = CardModelBuilder.Build(Inputs(agOpen: false, agLimits: Ag(5, fetchedAt: T0.AddMinutes(-1))), ClaudeDataStatus.NeedsLogin, true);

        Assert.Equal("Sin datos todavía", m.UpdatedLine);
    }

    [Fact]
    public void Card_PastReset_SaysResetting()
    {
        var claude = Section(CardModelBuilder.Build(Inputs(claudeLimits: Claude(100, resets: T0.AddMinutes(-1))), ClaudeDataStatus.Ok, true), "Claude");

        Assert.Equal("reiniciando", claude.Rows[0].Detail);
    }

    [Fact]
    public void Card_NoData()
    {
        var m = CardModelBuilder.Build(Inputs(), ClaudeDataStatus.Loading, true);

        Assert.Equal("Sin datos todavía", m.UpdatedLine);
    }
}
