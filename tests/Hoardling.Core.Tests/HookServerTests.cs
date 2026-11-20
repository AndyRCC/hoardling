using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Hoardling.Core;

namespace Hoardling.Core.Tests;

public class HookServerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<HttpStatusCode> PostAsync(int port, string body)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/hoardling",
            new StringContent(body, Encoding.UTF8, "application/json"));
        return response.StatusCode;
    }

    [Fact]
    public async Task Post_DeliversBodyAndReturns200()
    {
        var port = FreePort();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new HookServer(port, body => received.TrySetResult(body));
        Assert.True(server.TryStart());

        var status = await PostAsync(port, """{"hook_event_name":"Stop","session_id":"ñ"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"hook_event_name":"Stop","session_id":"ñ"}""", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Garbage_DoesNotCrash()
    {
        var port = FreePort();
        var count = 0;
        await using var server = new HookServer(port, _ => Interlocked.Increment(ref count));
        Assert.True(server.TryStart());

        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, port);
            await raw.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\u0001\u0002 garbage without headers"));
        }

        Assert.Equal(HttpStatusCode.OK, await PostAsync(port, "{}"));
        await Task.Delay(100);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TooLarge_Returns200AndDeliversTheEventEssentials()
    {
        // Review finding 8: a huge PostToolUse must not show a hook error nor lose the event.
        var port = FreePort();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new HookServer(port, body => received.TrySetResult(body));
        Assert.True(server.TryStart());
        var huge = "{\"session_id\":\"s1\",\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"Read\",\"tool_response\":\""
                   + new string('x', 2_000_000) + "\"}";

        var status = await PostAsync(port, huge);

        Assert.Equal(HttpStatusCode.OK, status);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)))!;
        Assert.Equal("PostToolUse", body["hook_event_name"]!.GetValue<string>());
        Assert.Equal("s1", body["session_id"]!.GetValue<string>());
        Assert.Equal("Read", body["tool_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task LargeBodyOver16KB_DeliveredIntact()
    {
        var port = FreePort();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new HookServer(port, body => received.TrySetResult(body));
        Assert.True(server.TryStart());
        var body = "{\"hook_event_name\":\"PostToolUse\",\"pad\":\"" + new string('y', 100_000) + "\"}";

        Assert.Equal(HttpStatusCode.OK, await PostAsync(port, body));
        Assert.Equal(body, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MissingContentLength_StillReturns200()
    {
        var port = FreePort();
        await using var server = new HookServer(port, _ => { });
        Assert.True(server.TryStart());

        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        var stream = raw.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /hoardling HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        var buffer = new byte[256];
        var n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(buffer, 0, n));
    }

    [Fact]
    public async Task Events_ProcessedInArrivalOrder_EvenWhenHandlerIsSlow()
    {
        // Review finding 4: Claude Code sends the next hook as soon as it gets our reply.
        var port = FreePort();
        var order = new List<string>();
        await using var server = new HookServer(port, body =>
        {
            if (body == "\"first\"")
            {
                Thread.Sleep(300);
            }
            lock (order)
            {
                order.Add(body);
            }
        });
        Assert.True(server.TryStart());

        // Raw sockets: like a fast client, read the status line as soon as it arrives.
        await RawPostReadStatusAsync(port, "\"first\"");
        await RawPostReadStatusAsync(port, "\"second\"");
        await Task.Delay(500);

        Assert.Equal(["\"first\"", "\"second\""], order);
    }

    private static async Task RawPostReadStatusAsync(int port, string body)
    {
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        var stream = raw.GetStream();
        var bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /hoardling HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {bytes.Length}\r\n\r\n"));
        await stream.WriteAsync(bytes);
        var buffer = new byte[64];
        await stream.ReadAtLeastAsync(buffer, 12).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SilentClient_DoesNotBlockOthers()
    {
        var port = FreePort();
        await using var server = new HookServer(port, _ => { });
        Assert.True(server.TryStart());
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, port);

        var watch = Stopwatch.StartNew();
        var status = await PostAsync(port, "{}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task HandlerThrows_StillReturns200()
    {
        var port = FreePort();
        await using var server = new HookServer(port, _ => throw new InvalidOperationException("boom"));
        Assert.True(server.TryStart());

        Assert.Equal(HttpStatusCode.OK, await PostAsync(port, "{}"));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(port, "{}"));
    }

    [Fact]
    public async Task PortBusy_TryStartFalse()
    {
        var port = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            await using var server = new HookServer(port, _ => { });
            Assert.False(server.TryStart());
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void ProcessCheck_FindsCurrentProcess()
    {
        using var me = Process.GetCurrentProcess();

        Assert.True(ProcessCheck.AnyRunning("definitely-not-running-xyz", me.ProcessName));
        Assert.False(ProcessCheck.AnyRunning("definitely-not-running-xyz"));
    }
}
