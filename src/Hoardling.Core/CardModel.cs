namespace Hoardling.Core;

public enum ClaudeDataStatus { Ok, NoCredentials, NeedsLogin, Error, Loading }

public sealed record CardRow(string Label, double? UsedPercent, string? Detail);

/// <summary>Value equality includes the rows (records compare lists by reference otherwise).</summary>
public sealed record CardSection(string Title, string? Plan, IReadOnlyList<CardRow> Rows, string? Notice)
{
    public bool Equals(CardSection? other) =>
        other is not null && Title == other.Title && Plan == other.Plan && Notice == other.Notice && Rows.SequenceEqual(other.Rows);

    public override int GetHashCode() => HashCode.Combine(Title, Plan, Notice, Rows.Count);
}

/// <summary>Value equality includes the sections, so the coordinator can skip identical updates.</summary>
public sealed record CardModel(IReadOnlyList<CardSection> Sections, string StatusLine, string UpdatedLine)
{
    public bool Equals(CardModel? other) =>
        other is not null && StatusLine == other.StatusLine && UpdatedLine == other.UpdatedLine && Sections.SequenceEqual(other.Sections);

    public override int GetHashCode() => HashCode.Combine(StatusLine, UpdatedLine, Sections.Count);
}

/// <summary>Builds the hover card text. Spec section "Tarjeta al pasar el mouse".</summary>
public static class CardModelBuilder
{
    public const string NothingOpenHint = "Abre Claude o Antigravity para ver tus límites";
    public const string LoginNotice = "Inicia sesión: abre una terminal, escribe claude y luego /login";

    public static CardModel Build(PetInputs i, ClaudeDataStatus claudeStatus, bool hookServerOk, string? warning = null)
    {
        // Only open apps: a closed app's numbers may be stale (its limits can be used on another device).
        var sections = new List<CardSection>(2);
        if (i.ClaudeOpen)
        {
            sections.Add(ClaudeSection(i, claudeStatus));
        }
        if (i.AgOpen)
        {
            sections.Add(AgSection(i));
        }
        var status = $"Claude: {Word(i.ClaudeOpen, i.Claude)} · Antigravity: {Word(i.AgOpen, i.Ag)}";
        if (!hookServerOk)
        {
            status += " · sin actividad de Claude (puerto ocupado)";
        }
        if (!string.IsNullOrEmpty(warning))
        {
            status += " · " + warning;
        }
        return new CardModel(sections, status, UpdatedLine(i));
    }

    private static CardSection ClaudeSection(PetInputs i, ClaudeDataStatus status)
    {
        var fresh = PetStateMachine.Fresh(i.ClaudeLimits, i.Now);
        var notice = status switch
        {
            ClaudeDataStatus.NeedsLogin or ClaudeDataStatus.NoCredentials => LoginNotice,
            ClaudeDataStatus.Error => "No se pudo leer el uso de Claude",
            ClaudeDataStatus.Loading => "Cargando…",
            _ => null,
        };
        var rows = fresh is null ? [] : fresh.Groups.SelectMany(g => g.Buckets).Select(b => Row(b.Label, b, i.Now)).ToList();
        if (rows.Count == 0 && notice is null)
        {
            notice = "Sin datos recientes";
        }
        return new CardSection("Claude", i.ClaudeLimits?.PlanName, rows, notice);
    }

    private static CardSection AgSection(PetInputs i)
    {
        var fresh = PetStateMachine.Fresh(i.AgLimits, i.Now);
        if (fresh is null)
        {
            var notice = i.AgLimits is null ? "Cargando…" : "No se pudo leer el uso de Antigravity";
            return new CardSection("Antigravity", i.AgLimits?.PlanName, [], notice);
        }
        var rows = fresh.Groups
            .SelectMany(g => g.Buckets.Select(b => Row($"{g.Name} · {b.Label}", b, i.Now)))
            .ToList();
        return new CardSection("Antigravity", fresh.PlanName, rows, null);
    }

    private static CardRow Row(string label, LimitBucket b, DateTimeOffset now)
    {
        string? detail = null;
        if (b.ResetsAt is { } resets)
        {
            var left = TimeFormat.Until(resets, now);
            detail = left == "ya" ? "reiniciando" : $"se reinicia en {left}";
        }
        return new CardRow(label, b.UsedPercent, detail);
    }

    private static string Word(bool open, AppActivity activity) => !open
        ? "cerrado"
        : activity switch
        {
            AppActivity.NeedsYou => "te necesita",
            AppActivity.Thinking or AppActivity.Typing => "trabajando",
            _ => "en reposo",
        };

    private static string UpdatedLine(PetInputs i)
    {
        if (!i.ClaudeOpen && !i.AgOpen)
        {
            return NothingOpenHint;
        }
        DateTimeOffset? latest = null;
        foreach (var limits in new[] { i.ClaudeOpen ? i.ClaudeLimits : null, i.AgOpen ? i.AgLimits : null })
        {
            if (limits is not null && (latest is null || limits.FetchedAt > latest))
            {
                latest = limits.FetchedAt;
            }
        }
        if (latest is null)
        {
            return "Sin datos todavía";
        }
        var age = i.Now - latest.Value;
        return age < TimeSpan.FromMinutes(1) ? "Actualizado hace un momento" : $"Actualizado hace {(int)age.TotalMinutes} min";
    }
}
