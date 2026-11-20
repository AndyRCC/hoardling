using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hoardling.Core;

namespace Hoardling.Core.Tests;

/// <summary>Tests added by the pre-release audit (help-by-AG/review-release.md).</summary>
public class ReleaseHardeningTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Sends a raw request and returns the status line, or "" if the server closed without answering.</summary>
    private static async Task<string> RawAsync(int port, string head, string body)
    {
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        var stream = raw.GetStream();
        var bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{head}Content-Length: {bytes.Length}\r\n\r\n"));
        await stream.WriteAsync(bytes);
        var buffer = new byte[64];
        var n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.ASCII.GetString(buffer, 0, n);
    }

    [Theory]
    [InlineData("POST /hoardling HTTP/1.1\r\nHost: 127.0.0.1\r\nOrigin: https://evil.example\r\n")] // a web page (CSRF)
    [InlineData("POST /hoardling HTTP/1.1\r\nHost: evil.example:47823\r\n")]                          // DNS rebinding
    [InlineData("POST /something-else HTTP/1.1\r\nHost: 127.0.0.1\r\n")]                               // wrong path
    [InlineData("GET /hoardling HTTP/1.1\r\nHost: 127.0.0.1\r\n")]                                     // wrong method
    [InlineData("POST /dragonpet HTTP/1.1\r\nHost: 127.0.0.1\r\n")]                                    // pre-rename path
    public async Task ForeignRequests_Get200ButAreNotDelivered(string head)
    {
        var port = FreePort();
        var called = false;
        await using var server = new HookServer(port, _ => called = true);
        Assert.True(server.TryStart());

        var status = await RawAsync(port, head, """{"hook_event_name":"Stop","session_id":"s1"}""");

        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.False(called);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:47823")]
    [InlineData("localhost:47823")]
    public async Task LoopbackHosts_AreDelivered(string host)
    {
        var port = FreePort();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new HookServer(port, body => received.TrySetResult(body));
        Assert.True(server.TryStart());

        await RawAsync(port, $"POST /hoardling HTTP/1.1\r\nHost: {host}\r\n", "{}");

        Assert.Equal("{}", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TooLarge_SubagentEvent_KeepsAgentId_AndSurvivesABadEscape()
    {
        // Without agent_id, a huge subagent PostToolUse would bring back the "always thinking" bug.
        var port = FreePort();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new HookServer(port, body => received.TrySetResult(body));
        Assert.True(server.TryStart());
        var huge = "{\"session_id\":\"s1\",\"agent_id\":\"a-7\",\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"bad\\uZZZZ\",\"tool_response\":\""
                   + new string('x', 2_000_000) + "\"}";

        var status = await RawAsync(port, "POST /hoardling HTTP/1.1\r\nHost: 127.0.0.1\r\n", huge);
        var delivered = JsonNode.Parse(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)))!;

        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.Equal("a-7", delivered["agent_id"]!.GetValue<string>());
        Assert.Equal(HookEventResult.Ignored, new ClaudeActivityTracker().OnHookEvent(delivered.ToJsonString(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Tracker_KeepsAtMostMaxSessions_EvictingTheOldest()
    {
        var t = new ClaudeActivityTracker();
        var start = new DateTimeOffset(2026, 11, 19, 2, 0, 0, TimeSpan.Zero);
        t.OnHookEvent("""{"hook_event_name":"PermissionRequest","session_id":"oldest"}""", start);
        for (var i = 1; i <= ClaudeActivityTracker.MaxSessions + 50; i++)
        {
            var json = JsonSerializer.Serialize(new { hook_event_name = "UserPromptSubmit", session_id = $"s{i}" });
            t.OnHookEvent(json, start.AddSeconds(i));
        }

        Assert.Equal(ClaudeActivityTracker.MaxSessions, t.SessionCount);
        // "oldest" (NeedsYou) was the one evicted, so only the newer Thinking sessions remain.
        Assert.Equal(AppActivity.Thinking, t.GetActivity(start.AddSeconds(ClaudeActivityTracker.MaxSessions + 50)));
    }

    [Fact]
    public void ProcessCheck_PrefixAndFolder()
    {
        // Antigravity's language server may be named language_server.exe or language_server_windows_x64.exe.
        using var me = Process.GetCurrentProcess();
        var prefix = me.ProcessName[..Math.Min(4, me.ProcessName.Length)];
        var folder = Path.GetFileName(Path.GetDirectoryName(me.MainModule!.FileName)!);

        Assert.True(ProcessCheck.AnyRunningPrefix(prefix, folder));
        Assert.False(ProcessCheck.AnyRunningPrefix(prefix, "definitely-not-this-folder"));
        Assert.False(ProcessCheck.AnyRunningPrefix("definitely-not-a-process-", null));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Hoardling\Hoardling.exe")]
    [InlineData(@"C:\My Folder\")]          // trailing backslash must not escape the closing quote
    [InlineData("a\\\" --injected")]        // a quote after a backslash must not end the argument
    [InlineData("tab\there")]
    [InlineData("")]
    [InlineData("plain")]
    public void Quote_RoundTripsThroughWindowsParser(string arg)
    {
        var parsed = ParseWindowsCommandLine("prog.exe " + WindowsCommandLine.Quote(arg) + " --next");

        Assert.Equal(["prog.exe", arg, "--next"], parsed);
    }

    private static string[] ParseWindowsCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [Fact]
    public void Installer_NoLongerAddsSubagentStop_ButStillRemovesALegacyOne()
    {
        Assert.DoesNotContain("SubagentStop", ClaudeHooksInstaller.Events);

        var legacy = """{"hooks":{"SubagentStop":[{"hooks":[{"type":"http","url":"http://127.0.0.1:47823/dragonpet","timeout":2}]}]}}""";
        var cleaned = ClaudeHooksInstaller.Uninstall(legacy, out var changed);

        Assert.True(changed);
        Assert.Null(JsonNode.Parse(cleaned)!["hooks"]);
    }
}
