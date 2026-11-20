using Hoardling.Core;

namespace Hoardling.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 19, 2, 0, 0, TimeSpan.Zero);

    public static ProviderLimits Claude(double fiveHour, double weekly = 0, DateTimeOffset? fetchedAt = null, DateTimeOffset? resets = null) =>
        new("Pro",
            [new LimitGroup("Claude",
            [
                new LimitBucket("Sesión 5 h", LimitWindow.FiveHour, fiveHour, resets),
                new LimitBucket("Semana", LimitWindow.Weekly, weekly, null),
            ])],
            fetchedAt ?? T0);

    public static ProviderLimits Ag(double gemini5h, double claudeGpt5h = 0, DateTimeOffset? fetchedAt = null) =>
        new("Pro",
            [
                new LimitGroup("Gemini",
                [
                    new LimitBucket("5 h", LimitWindow.FiveHour, gemini5h, null),
                    new LimitBucket("Semana", LimitWindow.Weekly, 4, null),
                ]),
                new LimitGroup("Claude y GPT", [new LimitBucket("5 h", LimitWindow.FiveHour, claudeGpt5h, null)]),
            ],
            fetchedAt ?? T0);

    public static PetInputs Inputs(
        bool claudeOpen = true,
        bool agOpen = true,
        AppActivity claude = AppActivity.Idle,
        AppActivity ag = AppActivity.Idle,
        DateTimeOffset? claudeFinishedAt = null,
        DateTimeOffset? agFinishedAt = null,
        ProviderLimits? claudeLimits = null,
        ProviderLimits? agLimits = null,
        DateTimeOffset? now = null) =>
        new(claudeOpen, agOpen, claude, ag, claudeFinishedAt, agFinishedAt, claudeLimits, agLimits, now ?? T0);
}
