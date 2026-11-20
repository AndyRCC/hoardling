using System.Text.Json;
using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class ClaudeActivityTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 2, 0, 0, TimeSpan.Zero);

    private static string Ev(string name, string session = "s1", string? tool = null, string? notification = null) =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["hook_event_name"] = name,
            ["session_id"] = session,
            ["tool_name"] = tool,
            ["notification_type"] = notification,
        });

    [Fact]
    public void Prompt_Thinking()
    {
        var t = new ClaudeActivityTracker();

        Assert.Equal(HookEventResult.Activity, t.OnHookEvent(Ev("UserPromptSubmit"), T0));
        Assert.Equal(AppActivity.Thinking, t.GetActivity(T0));
    }

    [Fact]
    public void PreToolUse_Typing()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("PreToolUse", tool: "Bash"), T0);

        Assert.Equal(AppActivity.Typing, t.GetActivity(T0));
    }

    [Fact]
    public void PostToolUse_BackToThinking()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("PreToolUse", tool: "Bash"), T0);
        t.OnHookEvent(Ev("PostToolUse", tool: "Bash"), T0);

        Assert.Equal(AppActivity.Thinking, t.GetActivity(T0));
    }

    [Fact]
    public void PreToolUse_AskUserQuestion_NeedsYou()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("PreToolUse", tool: "AskUserQuestion"), T0);

        Assert.Equal(AppActivity.NeedsYou, t.GetActivity(T0));
    }

    [Fact]
    public void PermissionRequest_NeedsYou()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("PermissionRequest", tool: "Bash"), T0);

        Assert.Equal(AppActivity.NeedsYou, t.GetActivity(T0));
    }

    [Theory]
    [InlineData("permission_prompt")]
    [InlineData("elicitation_dialog")]
    public void Notification_PermissionPrompt_NeedsYou(string type)
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("UserPromptSubmit"), T0);
        t.OnHookEvent(Ev("Notification", notification: type), T0);

        Assert.Equal(AppActivity.NeedsYou, t.GetActivity(T0));
    }

    [Fact]
    public void Notification_LegacyMessageAboutPermission_NeedsYou()
    {
        var t = new ClaudeActivityTracker();
        var json = """{"hook_event_name":"Notification","session_id":"s1","message":"Claude needs your permission to use Bash"}""";

        t.OnHookEvent(json, T0);

        Assert.Equal(AppActivity.NeedsYou, t.GetActivity(T0));
    }

    [Fact]
    public void Notification_IdlePrompt_Ignored()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("Stop"), T0);

        Assert.Equal(HookEventResult.Ignored, t.OnHookEvent(Ev("Notification", notification: "idle_prompt"), T0));
        Assert.Equal(AppActivity.Idle, t.GetActivity(T0));
    }

    [Fact]
    public void NeedsYou_ClearedByNextActivity()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("PermissionRequest", tool: "Bash"), T0);
        t.OnHookEvent(Ev("PreToolUse", tool: "Bash"), T0);

        Assert.Equal(AppActivity.Typing, t.GetActivity(T0));
    }

    [Fact]
    public void Stop_IdleAndSetsFinished()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("UserPromptSubmit"), T0);

        Assert.Equal(HookEventResult.Stop, t.OnHookEvent(Ev("Stop"), T0.AddSeconds(5)));
        Assert.Equal(AppActivity.Idle, t.GetActivity(T0.AddSeconds(5)));
        Assert.Equal(T0.AddSeconds(5), t.LastFinishedAt);
    }

    private static string ForkEv(string name, string? tool = null) =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["hook_event_name"] = name,
            ["session_id"] = "s1",
            ["agent_id"] = "a-7",
            ["tool_name"] = tool,
        });

    [Fact]
    public void SubagentStop_DoesNotChangeTheMainSession()
    {
        // The main agent is still inside its Agent tool call; its PostToolUse will follow.
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("PreToolUse", tool: "Agent"), T0);

        Assert.Equal(HookEventResult.Ignored, t.OnHookEvent(ForkEv("SubagentStop"), T0));
        Assert.Equal(AppActivity.Typing, t.GetActivity(T0));
        Assert.Null(t.LastFinishedAt);
    }

    [Fact]
    public void BackgroundForkAfterStop_StaysIdle()
    {
        // Bug "¿por qué está pensando?": after each reply the desktop app runs a background fork
        // (next-prompt suggestion) that fires SubagentStop with the same session_id.
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("UserPromptSubmit"), T0);
        t.OnHookEvent(Ev("Stop"), T0.AddSeconds(5));

        t.OnHookEvent(ForkEv("SubagentStop"), T0.AddSeconds(8));

        Assert.Equal(AppActivity.Idle, t.GetActivity(T0.AddSeconds(9)));
    }

    [Fact]
    public void EventsFromSubagents_AreIgnored()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("Stop"), T0);

        Assert.Equal(HookEventResult.Ignored, t.OnHookEvent(ForkEv("PreToolUse", tool: "Bash"), T0));
        Assert.Equal(HookEventResult.Ignored, t.OnHookEvent(ForkEv("PostToolUse", tool: "Bash"), T0));
        Assert.Equal(AppActivity.Idle, t.GetActivity(T0));
    }

    [Fact]
    public void SessionEnd_RemovesSession()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("UserPromptSubmit"), T0);
        t.OnHookEvent(Ev("SessionEnd"), T0);

        Assert.Equal(AppActivity.Idle, t.GetActivity(T0));
    }

    [Fact]
    public void TwoSessions_NeedsYouWins()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("PreToolUse", "a", tool: "Bash"), T0);
        t.OnHookEvent(Ev("PermissionRequest", "b", tool: "Bash"), T0);

        Assert.Equal(AppActivity.NeedsYou, t.GetActivity(T0));
    }

    [Fact]
    public void TwoSessions_TypingBeatsThinking()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("UserPromptSubmit", "a"), T0);
        t.OnHookEvent(Ev("PreToolUse", "b", tool: "Edit"), T0);

        Assert.Equal(AppActivity.Typing, t.GetActivity(T0));
    }

    [Fact]
    public void StaleSession_After15Min_Idle()
    {
        var t = new ClaudeActivityTracker();
        t.OnHookEvent(Ev("PreToolUse", tool: "Bash"), T0);

        Assert.Equal(AppActivity.Typing, t.GetActivity(T0.AddMinutes(15)));
        Assert.Equal(AppActivity.Idle, t.GetActivity(T0.AddMinutes(15).AddSeconds(1)));
    }

    [Theory]
    [InlineData("{oops")]
    [InlineData("[]")]
    [InlineData("""{"session_id":"s1"}""")]
    [InlineData("""{"hook_event_name":"Bogus","session_id":"s1"}""")]
    public void InvalidOrUnknown_Ignored(string json)
    {
        var t = new ClaudeActivityTracker();

        Assert.Equal(HookEventResult.Ignored, t.OnHookEvent(json, T0));
        Assert.Equal(AppActivity.Idle, t.GetActivity(T0));
    }
}
