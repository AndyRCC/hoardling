namespace Hoardling.Core;

/// <summary>Ordered by priority: aggregation keeps the highest value across sessions.</summary>
public enum AppActivity { Idle, Thinking, Typing, NeedsYou }

public enum HookEventResult { Ignored, Activity, Stop }

/// <summary>
/// Turns Claude Code hook events into an activity per session and aggregates them.
/// Thread-safe: events arrive on the hook server thread, reads come from the coordinator.
/// </summary>
public sealed class ClaudeActivityTracker
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public const int MaxSessions = 64;

    internal int SessionCount
    {
        get { lock (_gate) { return _sessions.Count; } }
    }

    private enum SessionState { Thinking, Typing, NeedsYou, Finished }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (SessionState State, DateTimeOffset At)> _sessions = new();
    private DateTimeOffset? _lastFinishedAt;

    public DateTimeOffset? LastFinishedAt
    {
        get { lock (_gate) { return _lastFinishedAt; } }
    }

    public HookEventResult OnHookEvent(string json, DateTimeOffset now)
    {
        var root = JsonRead.TryParseObject(json);
        var name = JsonRead.String(root?["hook_event_name"]);
        var session = JsonRead.String(root?["session_id"]);
        if (root is null || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(session))
        {
            return HookEventResult.Ignored;
        }
        // Subagents and background forks (e.g. the desktop app's next-prompt suggestion, which runs
        // after every reply) report with the same session_id plus an agent_id. They are not the
        // conversation the user is waiting on: the main agent's own events already cover a real subagent.
        if (name == "SubagentStop" || !string.IsNullOrEmpty(JsonRead.String(root["agent_id"])))
        {
            return HookEventResult.Ignored;
        }

        SessionState? next = name switch
        {
            "UserPromptSubmit" or "PostToolUse" => SessionState.Thinking,
            "PreToolUse" => JsonRead.String(root["tool_name"]) == "AskUserQuestion" ? SessionState.NeedsYou : SessionState.Typing,
            "PermissionRequest" => SessionState.NeedsYou,
            "Notification" => IsAttentionNotification(root) ? SessionState.NeedsYou : null,
            "Stop" => SessionState.Finished,
            _ => null,
        };

        lock (_gate)
        {
            if (name == "SessionEnd")
            {
                _sessions.Remove(session);
                return HookEventResult.Activity;
            }
            if (next is null)
            {
                return HookEventResult.Ignored;
            }
            if (_sessions.Count >= MaxSessions && !_sessions.ContainsKey(session))
            {
                // Bounded memory even if sessions never send SessionEnd: forget the least recent one.
                _sessions.Remove(_sessions.MinBy(p => p.Value.At).Key);
            }
            _sessions[session] = (next.Value, now);
            if (next == SessionState.Finished)
            {
                _lastFinishedAt = now;
                return HookEventResult.Stop;
            }
            return HookEventResult.Activity;
        }
    }

    public AppActivity GetActivity(DateTimeOffset now)
    {
        lock (_gate)
        {
            var result = AppActivity.Idle;
            List<string>? stale = null;
            foreach (var (id, (state, at)) in _sessions)
            {
                if (now - at > StaleAfter)
                {
                    (stale ??= []).Add(id);
                    continue;
                }
                var activity = state switch
                {
                    SessionState.NeedsYou => AppActivity.NeedsYou,
                    SessionState.Typing => AppActivity.Typing,
                    SessionState.Thinking => AppActivity.Thinking,
                    _ => AppActivity.Idle,
                };
                if (activity > result)
                {
                    result = activity;
                }
            }
            stale?.ForEach(id => _sessions.Remove(id));
            return result;
        }
    }

    private static bool IsAttentionNotification(System.Text.Json.Nodes.JsonObject root)
    {
        var type = JsonRead.String(root["notification_type"]);
        if (type is not null)
        {
            return type is "permission_prompt" or "elicitation_dialog";
        }
        // Older Claude Code versions send only a message.
        return JsonRead.String(root["message"])?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true;
    }
}
