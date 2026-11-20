using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hoardling.Core;

public enum HookInstallOutcome { Installed, AlreadyInstalled, Removed, NotInstalled, InvalidSettings }

/// <summary>
/// Adds or removes the dragon's hooks in Claude Code's settings JSON without touching anything else.
/// Pure string-in/string-out; see <see cref="ClaudeSettingsFile"/> for the file side.
/// </summary>
public static class ClaudeHooksInstaller
{
    public const string HookUrl = "http://127.0.0.1:47823" + HookServer.Path;

    // Before its first release Hoardling was called DragonPet: its hooks may still sit in a user's settings.
    private const string LegacyHookUrl = "http://127.0.0.1:47823/dragonpet";

    // No SubagentStop: subagent and background-fork events are ignored by the tracker, so listening to them
    // only costs a request per fork. Uninstall still removes a SubagentStop hook left by older versions.
    public static readonly IReadOnlyList<string> Events =
    [
        "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest",
        "Notification", "Stop", "SessionEnd",
    ];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The only place where the hook handler is defined.</summary>
    public static JsonObject CreateHandler() => new()
    {
        ["type"] = "http",
        ["url"] = HookUrl,
        ["timeout"] = 2,
    };

    public static string Install(string? settingsJson, out bool changed)
    {
        var root = ParseRoot(settingsJson);
        var hooks = ObjectProperty(root, "hooks");
        // Replace hooks left by the old name instead of adding ours next to them.
        changed = RemoveHandlers(hooks, IsLegacy);
        foreach (var evt in Events)
        {
            var groups = ArrayProperty(hooks, evt);
            if (groups.Any(ContainsCurrent))
            {
                continue;
            }
            groups.Add(new JsonObject { ["hooks"] = new JsonArray(CreateHandler()) });
            changed = true;
        }
        return changed ? root.ToJsonString(WriteOptions) : settingsJson!;
    }

    public static string Uninstall(string settingsJson, out bool changed)
    {
        var root = ParseRoot(settingsJson);
        changed = false;
        if (root["hooks"] is not JsonObject hooks)
        {
            return settingsJson;
        }
        changed = RemoveHandlers(hooks, IsOurs);
        if (!changed)
        {
            return settingsJson;
        }
        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Removes matching handlers, then groups and events left empty. True if anything was removed.</summary>
    private static bool RemoveHandlers(JsonObject hooks, Func<JsonObject, bool> match)
    {
        var changed = false;
        foreach (var evt in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[evt] is not JsonArray groups)
            {
                continue;
            }
            var removedInEvent = false;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray handlers)
                {
                    continue;
                }
                var ours = handlers.OfType<JsonObject>().Where(match).ToList();
                if (ours.Count == 0)
                {
                    continue;
                }
                ours.ForEach(h => handlers.Remove(h));
                removedInEvent = true;
                if (handlers.Count == 0)
                {
                    groups.Remove(group);
                }
            }
            if (removedInEvent && groups.Count == 0)
            {
                hooks.Remove(evt);
            }
            changed |= removedInEvent;
        }
        return changed;
    }

    public static bool IsInstalled(string? settingsJson)
    {
        try
        {
            var root = ParseRoot(settingsJson);
            return root["hooks"] is JsonObject hooks
                && Events.All(evt => hooks[evt] is JsonArray groups && groups.Any(ContainsCurrent));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static JsonObject ParseRoot(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonRead.ParseObject(json);

    private static JsonObject ObjectProperty(JsonObject parent, string name)
    {
        switch (parent[name])
        {
            case null:
                var created = new JsonObject();
                parent[name] = created;
                return created;
            case JsonObject existing:
                return existing;
            default:
                throw new FormatException($"\"{name}\" is not an object.");
        }
    }

    private static JsonArray ArrayProperty(JsonObject parent, string name)
    {
        switch (parent[name])
        {
            case null:
                var created = new JsonArray();
                parent[name] = created;
                return created;
            case JsonArray existing:
                return existing;
            default:
                throw new FormatException($"\"{name}\" is not an array.");
        }
    }

    // Pattern-match before indexing: indexing a JsonValue/JsonArray by name throws InvalidOperationException.
    private static bool ContainsCurrent(JsonNode? group) =>
        group is JsonObject g && g["hooks"] is JsonArray handlers && handlers.OfType<JsonObject>().Any(IsCurrent);

    private static bool IsCurrent(JsonObject handler) => JsonRead.String(handler["url"]) == HookUrl;

    private static bool IsLegacy(JsonObject handler)
    {
        if (JsonRead.String(handler["url"]) == LegacyHookUrl)
        {
            return true;
        }
        // An early development build of DragonPet ran itself as a command hook.
        var command = JsonRead.String(handler["command"]);
        return command is not null
            && command.Contains("DragonPet", StringComparison.OrdinalIgnoreCase)
            && command.Contains("--hook", StringComparison.Ordinal);
    }

    private static bool IsOurs(JsonObject handler) => IsCurrent(handler) || IsLegacy(handler);
}

/// <summary>Applies <see cref="ClaudeHooksInstaller"/> to a settings file, with a one-time backup.</summary>
public static class ClaudeSettingsFile
{
    public const string BackupSuffix = ".hoardling.bak";

    public static HookInstallOutcome EnsureHooks(string path)
    {
        string? original = File.Exists(path) ? File.ReadAllText(path) : null;
        string updated;
        bool changed;
        try
        {
            updated = ClaudeHooksInstaller.Install(original, out changed);
        }
        catch (FormatException)
        {
            return HookInstallOutcome.InvalidSettings;
        }
        if (!changed)
        {
            return HookInstallOutcome.AlreadyInstalled;
        }
        var backup = path + BackupSuffix;
        if (original is not null && !File.Exists(backup))
        {
            File.Copy(path, backup);
        }
        AtomicFile.WriteAllText(path, updated);
        return HookInstallOutcome.Installed;
    }

    public static HookInstallOutcome RemoveHooks(string path)
    {
        if (!File.Exists(path))
        {
            return HookInstallOutcome.NotInstalled;
        }
        string updated;
        bool changed;
        try
        {
            updated = ClaudeHooksInstaller.Uninstall(File.ReadAllText(path), out changed);
        }
        catch (FormatException)
        {
            return HookInstallOutcome.InvalidSettings;
        }
        if (!changed)
        {
            return HookInstallOutcome.NotInstalled;
        }
        AtomicFile.WriteAllText(path, updated);
        return HookInstallOutcome.Removed;
    }
}
