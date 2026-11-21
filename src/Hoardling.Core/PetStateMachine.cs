namespace Hoardling.Core;

public enum PetPalette { Mint, Sleepy, Claude, Antigravity, Fusion }

public enum PetPose { Idle, Sleeping, Thinking, Typing, NeedsYou, Happy }

public sealed record PetInputs(
    bool ClaudeOpen,
    bool AgOpen,
    AppActivity Claude,
    AppActivity Ag,
    DateTimeOffset? ClaudeFinishedAt,
    DateTimeOffset? AgFinishedAt,
    ProviderLimits? ClaudeLimits,
    ProviderLimits? AgLimits,
    DateTimeOffset Now);

public sealed record PetVisual(
    PetPalette Palette,
    PetPose Pose,
    bool Glasses,
    bool Sweat,
    bool Exhausted,
    double? ClaudeRing,
    double? AgRing,
    bool ClaudeRingShown,
    bool AgRingShown);

/// <summary>Decides what the dragon looks like. Rules: spec "Estados y prioridad".</summary>
public static class PetStateMachine
{
    public static readonly TimeSpan LimitsStaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan HappyFor = TimeSpan.FromSeconds(2);
    public const int AgPoseSwapSeconds = 5;
    public const double SweatAt = 80;

    public static PetVisual Compute(PetInputs i)
    {
        // A closed app's limits may be spent on another device meanwhile: show and count only open apps.
        var claudeLimits = i.ClaudeOpen ? Fresh(i.ClaudeLimits, i.Now) : null;
        var agLimits = i.AgOpen ? Fresh(i.AgLimits, i.Now) : null;
        var claudeRing = claudeLimits?.RingPercent();
        var agRing = agLimits?.RingPercent();

        if (!i.ClaudeOpen && !i.AgOpen)
        {
            return new PetVisual(PetPalette.Sleepy, PetPose.Sleeping, false, false, false, null, null, false, false);
        }

        var sweat = Math.Max(claudeRing ?? 0, agRing ?? 0) >= SweatAt;
        var exhausted = claudeLimits?.AnyExhausted == true || agLimits?.AnyExhausted == true;
        PetVisual Make(PetPalette palette, PetPose pose, bool glasses) =>
            new(palette, pose, glasses, sweat, exhausted, claudeRing, agRing, i.ClaudeOpen, i.AgOpen);

        // A closed app cannot be working, whatever its last events said.
        var claude = i.ClaudeOpen ? i.Claude : AppActivity.Idle;
        var ag = i.AgOpen ? i.Ag : AppActivity.Idle;

        bool claudeNeeds = claude == AppActivity.NeedsYou, agNeeds = ag == AppActivity.NeedsYou;
        if (claudeNeeds || agNeeds)
        {
            return Make(PaletteFor(claudeNeeds, agNeeds), PetPose.NeedsYou, false);
        }

        bool claudeWorks = claude is AppActivity.Thinking or AppActivity.Typing;
        bool agWorks = ag is AppActivity.Thinking or AppActivity.Typing;
        if (claudeWorks && agWorks)
        {
            return Make(PetPalette.Fusion, PetPose.Typing, true);
        }
        if (claudeWorks)
        {
            return Make(PetPalette.Claude, claude == AppActivity.Typing ? PetPose.Typing : PetPose.Thinking, true);
        }
        if (agWorks)
        {
            var phase = i.Now.ToUnixTimeSeconds() / AgPoseSwapSeconds % 2;
            return Make(PetPalette.Antigravity, phase == 0 ? PetPose.Thinking : PetPose.Typing, true);
        }

        bool claudeHappy = i.ClaudeOpen && JustFinished(i.ClaudeFinishedAt, i.Now);
        bool agHappy = i.AgOpen && JustFinished(i.AgFinishedAt, i.Now);
        if (claudeHappy || agHappy)
        {
            return Make(PaletteFor(claudeHappy, agHappy), PetPose.Happy, false);
        }

        return Make(PetPalette.Mint, PetPose.Idle, false);
    }

    /// <summary>Limits older than <see cref="LimitsStaleAfter"/> are treated as missing.</summary>
    public static ProviderLimits? Fresh(ProviderLimits? limits, DateTimeOffset now) =>
        limits is not null && now - limits.FetchedAt <= LimitsStaleAfter ? limits : null;

    // Bounded on both sides: if the wall clock jumps back, the dragon must not stay "happy" for minutes.
    private static bool JustFinished(DateTimeOffset? finishedAt, DateTimeOffset now) =>
        finishedAt is { } f && now - f < HappyFor && now - f > -HappyFor;

    private static PetPalette PaletteFor(bool claude, bool ag) =>
        claude && ag ? PetPalette.Fusion : claude ? PetPalette.Claude : PetPalette.Antigravity;
}
