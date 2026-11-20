using System.Text.Json.Nodes;
using Hoardling.Core;

namespace Hoardling.Core.Tests;

/// <summary>
/// Hoardling was called DragonPet before its first release; its hooks posted to /dragonpet.
/// Hooks left by that name (e.g. after the old pet was killed) must be replaced, never duplicated.
/// </summary>
public class RenameMigrationTests
{
    private const string LegacyUrl = "http://127.0.0.1:47823/dragonpet";

    private static JsonObject Handler(string url) => new() { ["type"] = "http", ["url"] = url, ["timeout"] = 2 };

    private static string LegacySettings()
    {
        var hooks = new JsonObject
        {
            ["Stop"] = new JsonArray(
                new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "echo listo" }) },
                new JsonObject { ["hooks"] = new JsonArray(Handler(LegacyUrl)) }),
        };
        foreach (var evt in ClaudeHooksInstaller.Events.Where(e => e != "Stop").Append("SubagentStop"))
        {
            hooks[evt] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(Handler(LegacyUrl)) });
        }
        return new JsonObject { ["model"] = "opus", ["hooks"] = hooks }.ToJsonString();
    }

    private static IEnumerable<string> Urls(string json) =>
        JsonNode.Parse(json)!["hooks"]!.AsObject()
            .SelectMany(p => p.Value!.AsArray())
            .SelectMany(g => g!["hooks"]!.AsArray())
            .Select(h => h!["url"]?.GetValue<string>())
            .OfType<string>();

    [Fact]
    public void HookUrl_UsesTheNewName()
    {
        Assert.Equal("http://127.0.0.1:47823/hoardling", ClaudeHooksInstaller.HookUrl);
        Assert.Equal("/hoardling", HookServer.Path);
    }

    [Fact]
    public void Install_ReplacesHooksLeftByTheOldName()
    {
        var json = ClaudeHooksInstaller.Install(LegacySettings(), out var changed);

        Assert.True(changed);
        Assert.DoesNotContain(LegacyUrl, Urls(json));
        Assert.Equal(ClaudeHooksInstaller.Events.Count, Urls(json).Count(u => u == ClaudeHooksInstaller.HookUrl));
        Assert.Null(JsonNode.Parse(json)!["hooks"]!["SubagentStop"]);
        Assert.Equal("echo listo", JsonNode.Parse(json)!["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(ClaudeHooksInstaller.IsInstalled(json));
    }

    [Fact]
    public void OnlyOldHooks_DoNotCountAsInstalled()
    {
        Assert.False(ClaudeHooksInstaller.IsInstalled(LegacySettings()));
    }

    [Fact]
    public void Uninstall_RemovesHooksLeftByTheOldName()
    {
        var json = ClaudeHooksInstaller.Uninstall(LegacySettings(), out var changed);

        Assert.True(changed);
        Assert.Empty(Urls(json));
        Assert.Equal(["Stop"], JsonNode.Parse(json)!["hooks"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void BackupUsesTheNewName()
    {
        Assert.Equal(".hoardling.bak", ClaudeSettingsFile.BackupSuffix);
    }
}
