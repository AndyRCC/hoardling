using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hoardling.Core;

/// <summary>
/// Minimal loopback HTTP/1.1 endpoint for Claude Code HTTP hooks. It always answers <c>200</c> with an
/// empty body, so it never shows a hook error nor changes Claude Code's behaviour. The event is handed to
/// <c>onEvent</c> before replying: Claude Code waits for the reply before sending its next hook, so
/// events are processed in order. <c>onEvent</c> must be fast (it only updates in-memory state).
/// A raw <see cref="TcpListener"/> avoids http.sys URL ACLs and Host-header matching.
/// </summary>
public sealed partial class HookServer(int port, Action<string> onEvent) : IAsyncDisposable
{
    /// <summary>The only path accepted; <see cref="ClaudeHooksInstaller.HookUrl"/> is built from it.</summary>
    public const string Path = "/hoardling";
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxBodyBytes = 1024 * 1024;
    private const int OversizePrefixBytes = 16 * 1024;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();
    private static readonly byte[] OkReply = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();

    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public bool TryStart()
    {
        if (_listener is not null)
        {
            return true;
        }
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Dispose();
            return false;
        }
        _listener = listener;
        // Off the caller's synchronization context (e.g. a UI thread), so connections never run there.
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        return true;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            // Bound concurrent connections: a local flood can't make it buffer hundreds of bodies at once.
            if (Interlocked.Increment(ref _active) > MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _active);
                client.Dispose();
                continue;
            }
            _ = HandleAsync(client, ct);
        }
    }

    private const int MaxConcurrentConnections = 16;
    private int _active;

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            // Never run a connection on the accept loop's thread, even when reads complete synchronously.
            await Task.Yield();
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ReadTimeout);
                try
                {
                    var stream = client.GetStream();
                    var body = await ReadRequestAsync(stream, timeout.Token);
                    if (body is not null)
                    {
                        Deliver(body);
                    }
                    await stream.WriteAsync(OkReply, timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                           or ObjectDisposedException or InvalidOperationException)
                {
                    // Broken or slow client: nothing to do.
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private void Deliver(string body)
    {
        try
        {
            onEvent(body);
        }
        catch (Exception)
        {
            // A faulty consumer must never take the listener down.
        }
    }

    /// <summary>Returns the body to deliver, or null when there is nothing usable.</summary>
    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);
        try
        {
            var filled = 0;
            int headerEnd;
            while ((headerEnd = buffer.AsSpan(0, filled).IndexOf(HeaderEnd)) < 0)
            {
                if (filled == MaxHeaderBytes)
                {
                    return null;
                }
                var n = await stream.ReadAsync(buffer.AsMemory(filled, MaxHeaderBytes - filled), ct);
                if (n == 0)
                {
                    return null;
                }
                filled += n;
            }

            var (accepted, length) = ParseHead(Encoding.ASCII.GetString(buffer, 0, headerEnd));
            if (length is not { } total)
            {
                return null;
            }
            var bodyStart = headerEnd + HeaderEnd.Length;
            var already = Math.Min(filled - bodyStart, total);

            if (!accepted)
            {
                // Read the body anyway: closing a socket with unread data sends a reset, and the
                // client could lose its 200 reply.
                await DrainAsync(stream, total - already, ct);
                return null;
            }

            if (total <= MaxBodyBytes)
            {
                var body = new byte[total];
                buffer.AsSpan(bodyStart, already).CopyTo(body);
                await stream.ReadExactlyAsync(body.AsMemory(already), ct);
                return Encoding.UTF8.GetString(body);
            }

            // Too big to keep (e.g. a tool that read a huge file): keep the start, drain the rest,
            // and deliver only the fields the dragon needs. Claude Code puts them before tool data.
            var prefix = new byte[OversizePrefixBytes];
            var kept = Math.Min(already, prefix.Length);
            buffer.AsSpan(bodyStart, kept).CopyTo(prefix);
            var remaining = total - already;
            var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (remaining > 0)
                {
                    var n = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), ct);
                    if (n == 0)
                    {
                        break;
                    }
                    if (kept < prefix.Length)
                    {
                        var take = Math.Min(n, prefix.Length - kept);
                        chunk.AsSpan(0, take).CopyTo(prefix.AsSpan(kept));
                        kept += take;
                    }
                    remaining -= n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
            return Essentials(Encoding.UTF8.GetString(prefix, 0, kept));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string? Essentials(string jsonPrefix)
    {
        var result = new JsonObject();
        foreach (Match m in EssentialField().Matches(jsonPrefix))
        {
            var key = m.Groups["k"].Value;
            if (result[key] is null && DecodeJsonString(m.Groups["v"].Value) is { } value)
            {
                result[key] = value;
            }
        }
        return result["hook_event_name"] is null ? null : result.ToJsonString();
    }

    /// <summary>Decodes a JSON string body (JSON escapes, not regex escapes); null if malformed.</summary>
    private static string? DecodeJsonString(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + raw + "\"");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // agent_id must survive: it marks subagent/background-fork events, which the tracker ignores.
    [GeneratedRegex("\"(?<k>hook_event_name|session_id|tool_name|notification_type|agent_id)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex EssentialField();

    private static async Task DrainAsync(NetworkStream stream, int remaining, CancellationToken ct)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                var n = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), ct);
                if (n == 0)
                {
                    return;
                }
                remaining -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// Whether we accept the request, and its Content-Length. Only a direct loopback <c>POST /hoardling</c>
    /// without an <c>Origin</c> header is accepted: a web page in a browser (which always sends Origin)
    /// or a DNS-rebinding host name cannot feed the dragon fake events.
    /// </summary>
    private static (bool Accepted, int? Length) ParseHead(string headers)
    {
        var lines = headers.Split("\r\n");
        var requestLine = lines[0];
        var accepted = requestLine.StartsWith("POST " + Path + " ", StringComparison.Ordinal)
                       || requestLine.StartsWith("POST " + Path + "?", StringComparison.Ordinal);
        int? length = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var name = line.AsSpan(0, colon).Trim();
            var value = line.AsSpan(colon + 1).Trim();
            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                || (name.Equals("Host", StringComparison.OrdinalIgnoreCase) && !IsLoopbackHost(value)))
            {
                accepted = false;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                length = parsed;
            }
        }
        return (accepted, length);
    }

    private static bool IsLoopbackHost(ReadOnlySpan<char> host)
    {
        var colon = host.LastIndexOf(':');
        var name = colon >= 0 && !host.StartsWith("[") ? host[..colon] : host;
        return name.Equals("127.0.0.1", StringComparison.Ordinal)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("[::1]", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        _listener?.Dispose();
        _cts.Dispose();
    }
}
