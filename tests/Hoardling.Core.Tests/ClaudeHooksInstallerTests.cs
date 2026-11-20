using System.Text.Json.Nodes;
using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class ClaudeHooksInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoardling-tests-" + Guid.NewGuid().ToString("N"));

    public ClaudeHooksInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string UserSettings = """
        {
          "model": "opus",
          "enabledPlugins": { "superpowers@claude-plugins-official": true },
          "hooks": {
            "Stop": [
              { "hooks": [ { "type": "command", "command": "echo listo" } ] }
            ]
          }
        }
        """;

    private static JsonArray Groups(string json, string evt) => (JsonArray)JsonNode.Parse(json)!["hooks"]![evt]!;

    private static int OurHandlers(string json, string evt) =>
        Groups(json, evt).SelectMany(g => g!["hooks"]!.AsArray())
            .Count(h => h!["url"]?.GetValue<string>() == ClaudeHooksInstaller.HookUrl);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    public void Install_EmptyOrNull_AddsAllEvents(string? input)
    {
        var json = ClaudeHooksInstaller.Install(input, out var changed);

        Assert.True(changed);
        foreach (var evt in ClaudeHooksInstaller.Events)
        {
            Assert.Equal(1, OurHandlers(json, evt));
        }
        var handler = Groups(json, "Stop")[0]!["hooks"]![0]!;
        Assert.Equal("http", handler["type"]!.GetValue<string>());
        Assert.Equal(2, handler["timeout"]!.GetValue<int>());
        Assert.True(ClaudeHooksInstaller.IsInstalled(json));
    }

    [Fact]
    public void Events_AreTheSpecList()
    {
        Assert.Equal(
            ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "SessionEnd"],
            ClaudeHooksInstaller.Events);
        Assert.Equal("http://127.0.0.1:47823/hoardling", ClaudeHooksInstaller.HookUrl);
    }

    [Fact]
    public void Install_PreservesUserContent()
    {
        var json = ClaudeHooksInstaller.Install(UserSettings, out _);
        var root = JsonNode.Parse(json)!;

        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.True(root["enabledPlugins"]!["superpowers@claude-plugins-official"]!.GetValue<bool>());
        var stop = Groups(json, "Stop");
        Assert.Equal(2, stop.Count);
        Assert.Equal("echo listo", stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(1, OurHandlers(json, "Stop"));
    }

    [Fact]
    public void Install_PreservesNonAsciiText()
    {
        var json = ClaudeHooksInstaller.Install("""{"note":"canción < > & ñ"}""", out _);

        Assert.Contains("\"canción < > & ñ\"", json);
    }

    [Fact]
    public void Install_Idempotent()
    {
        var once = ClaudeHooksInstaller.Install(UserSettings, out _);

        var twice = ClaudeHooksInstaller.Install(once, out var changed);

        Assert.False(changed);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Install_AddsOnlyMissingEvents()
    {
        var full = ClaudeHooksInstaller.Install("{}", out _);
        var root = JsonNode.Parse(full)!.AsObject();
        root["hooks"]!.AsObject().Remove("PreToolUse");

        var repaired = ClaudeHooksInstaller.Install(root.ToJsonString(), out var changed);

        Assert.True(changed);
        Assert.Equal(1, OurHandlers(repaired, "PreToolUse"));
        Assert.Equal(1, OurHandlers(repaired, "Stop"));
    }

    [Fact]
    public void Uninstall_RemovesOnlyOurs()
    {
        var installed = ClaudeHooksInstaller.Install(UserSettings, out _);

        var removed = ClaudeHooksInstaller.Uninstall(installed, out var changed);
        var root = JsonNode.Parse(removed)!;

        Assert.True(changed);
        Assert.False(ClaudeHooksInstaller.IsInstalled(removed));
        var hooks = root["hooks"]!.AsObject();
        Assert.Equal(["Stop"], hooks.Select(p => p.Key));
        Assert.Equal("echo listo", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Uninstall_DropsEmptyHooksObject()
    {
        var removed = ClaudeHooksInstaller.Uninstall(ClaudeHooksInstaller.Install("""{"model":"opus"}""", out _), out _);

        Assert.Null(JsonNode.Parse(removed)!["hooks"]);
    }

    [Fact]
    public void Uninstall_NothingToRemove_Unchanged()
    {
        var result = ClaudeHooksInstaller.Uninstall(UserSettings, out var changed);

        Assert.False(changed);
        Assert.Equal(UserSettings, result);
    }

    [Theory]
    [InlineData("{oops")]
    [InlineData("[]")]
    [InlineData("{ // comentario\n \"model\": \"opus\" }")]
    public void Install_InvalidJson_Throws(string json)
    {
        Assert.Throws<FormatException>(() => ClaudeHooksInstaller.Install(json, out _));
    }

    [Theory]
    [InlineData("""{"hooks":{"Stop":["x"]}}""")]
    [InlineData("""{"hooks":{"Stop":[[]]}}""")]
    [InlineData("""{"hooks":{"Stop":[{"hooks":"nope"}]}}""")]
    public void EnsureHooks_OddButValidJson_NeverThrows(string content)
    {
        // Review finding 7: a non-object hook group used to throw InvalidOperationException.
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, content);

        var outcome = ClaudeSettingsFile.EnsureHooks(path);

        Assert.Contains(outcome, new[] { HookInstallOutcome.Installed, HookInstallOutcome.InvalidSettings });
        Assert.False(ClaudeHooksInstaller.IsInstalled(content));
    }

    [Fact]
    public void Uninstall_RemovesCommandStyleHook()
    {
        const string json = """
            {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"\"C:\\x\\DragonPet.exe\" --hook","timeout":2}]}]}}
            """;

        var result = ClaudeHooksInstaller.Uninstall(json, out var changed);

        Assert.True(changed);
        Assert.Null(JsonNode.Parse(result)!["hooks"]);
    }

    [Fact]
    public void EnsureHooks_InvalidFile_DoesNotWrite()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ // comentario\n \"model\": \"opus\" }");
        var before = File.ReadAllBytes(path);

        Assert.Equal(HookInstallOutcome.InvalidSettings, ClaudeSettingsFile.EnsureHooks(path));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".hoardling.bak"));
    }

    [Fact]
    public void EnsureHooks_CreatesBackupOnce()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, UserSettings);

        Assert.Equal(HookInstallOutcome.Installed, ClaudeSettingsFile.EnsureHooks(path));
        Assert.Equal(UserSettings, File.ReadAllText(path + ".hoardling.bak"));
        Assert.True(ClaudeHooksInstaller.IsInstalled(File.ReadAllText(path)));

        Assert.Equal(HookInstallOutcome.Removed, ClaudeSettingsFile.RemoveHooks(path));
        Assert.Equal(HookInstallOutcome.Installed, ClaudeSettingsFile.EnsureHooks(path));
        Assert.Equal(UserSettings, File.ReadAllText(path + ".hoardling.bak"));
        Assert.Equal(HookInstallOutcome.AlreadyInstalled, ClaudeSettingsFile.EnsureHooks(path));
    }

    [Fact]
    public void EnsureHooks_MissingFile_Creates()
    {
        var path = Path.Combine(_dir, "nested", "settings.json");

        Assert.Equal(HookInstallOutcome.Installed, ClaudeSettingsFile.EnsureHooks(path));
        Assert.True(ClaudeHooksInstaller.IsInstalled(File.ReadAllText(path)));
        Assert.False(File.Exists(path + ".hoardling.bak"));
    }

    [Fact]
    public void RemoveHooks_MissingFile_NotInstalled()
    {
        Assert.Equal(HookInstallOutcome.NotInstalled, ClaudeSettingsFile.RemoveHooks(Path.Combine(_dir, "none.json")));
    }

    [Fact]
    public void EnsureHooks_LeavesNoTempFile()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{}");

        ClaudeSettingsFile.EnsureHooks(path);

        Assert.Equal(["settings.json", "settings.json.hoardling.bak"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }
}
