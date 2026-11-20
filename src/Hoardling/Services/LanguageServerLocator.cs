using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using Hoardling.Core;

namespace Hoardling.Services;

public sealed record LanguageServerEndpoint(int Pid, string CsrfToken, IReadOnlyList<int> Ports);

/// <summary>
/// Finds Antigravity's local language server: CSRF token from its command line (WMI) and its
/// listening ports (iphlpapi). Expensive (~100 ms), so callers cache the result.
/// </summary>
public static class LanguageServerLocator
{
    public static IReadOnlyList<LanguageServerEndpoint> FindAll()
    {
        var result = new List<LanguageServerEndpoint>();
        List<(int Pid, string Csrf, bool IsAntigravity)> candidates = [];
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name LIKE 'language_server%'");
            using var found = searcher.Get();
            foreach (var item in found)
            {
                using (item)
                {
                    var pid = Convert.ToInt32(item["ProcessId"]);
                    if (item["CommandLine"] is string cmd && AntigravityParser.ExtractCsrfToken(cmd) is { } csrf)
                    {
                        var path = item["ExecutablePath"] as string ?? "";
                        candidates.Add((pid, csrf, path.Contains("antigravity", StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return result;
        }

        if (candidates.Count == 0)
        {
            return result;
        }
        var ports = ListeningPortsByPid();
        // Prefer Antigravity's own language server when another Codeium-based editor's also runs.
        foreach (var (pid, csrf, _) in candidates.OrderByDescending(c => c.IsAntigravity))
        {
            if (ports.TryGetValue(pid, out var list) && list.Count > 0)
            {
                result.Add(new LanguageServerEndpoint(pid, csrf, list));
            }
        }
        return result;
    }

    private static Dictionary<int, List<int>> ListeningPortsByPid()
    {
        var map = new Dictionary<int, List<int>>();
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0)
        {
            return map;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (rc == ErrorInsufficientBuffer)
                {
                    continue;
                }
                if (rc != 0)
                {
                    return map;
                }
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + i * rowSize);
                    var port = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);
                    var pid = (int)row.OwningPid;
                    if (!map.TryGetValue(pid, out var list))
                    {
                        map[pid] = list = [];
                    }
                    if (!list.Contains(port))
                    {
                        list.Add(port); // the same port can be listed once per local address
                    }
                }
                return map;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return map;
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
