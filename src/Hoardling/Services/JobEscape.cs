using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Hoardling.Core;

namespace Hoardling.Services;

/// <summary>
/// A desktop pet must outlive whatever started it. Apps like Claude Code run their child processes
/// in Windows job objects and terminate them when they quit, which took the dragon with them.
/// If we start inside a job, relaunch once through WMI (the process is created by the WMI host, outside
/// the starter's jobs), falling back to CREATE_BREAKAWAY_FROM_JOB. <see cref="DetachedFlag"/> prevents loops.
/// </summary>
internal static class JobEscape
{
    public const string DetachedFlag = "--detached";
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary>How a detached copy was started ("breakaway" or "wmi"), or null to keep running here.</summary>
    public static string? TryRelaunchOutsideJob(string[] args)
    {
        if (args.Contains(DetachedFlag) || Environment.ProcessPath is not { } exe)
        {
            return null;
        }
        // Run as "dotnet Hoardling.dll": relaunching dotnet.exe alone would start nothing useful.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob) || !inJob)
        {
            return null;
        }
        // One-shot modes (--quit, --render-*, --probe-ag) never get here; --log is the only flag to keep.
        var commandLine = $"{WindowsCommandLine.Quote(exe)} {DetachedFlag}" + (args.Contains("--log") ? " --log" : "");
        var directory = Path.GetDirectoryName(exe);
        // WMI first: the new process lands where Explorer-launched apps live. Breakaway only leaves the
        // innermost job and, measured with Claude, stayed inside an outer job owned by the starter.
        if (TryWmi(commandLine, directory))
        {
            return "wmi";
        }
        return TryBreakaway(exe, commandLine, directory) ? "breakaway" : null;
    }

    private static bool TryBreakaway(string exe, string commandLine, string? directory)
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcess(exe, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                CreateBreakawayFromJob | CreateUnicodeEnvironment, IntPtr.Zero, directory, ref startup, out var info))
        {
            return false; // the job does not allow breakaway
        }
        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
        return true;
    }

    private static bool TryWmi(string commandLine, string? directory)
    {
        try
        {
            using var processClass = new ManagementClass("Win32_Process");
            using var input = processClass.GetMethodParameters("Create");
            input["CommandLine"] = commandLine;
            input["CurrentDirectory"] = directory;
            using var output = processClass.InvokeMethod("Create", input, null);
            return output?["ReturnValue"] is { } rc && Convert.ToUInt32(rc) == 0;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Diagnostics: limit flags of the job this process is in, e.g. "no job" or "killOnClose=False ...".</summary>
    public static string DescribeOwnJob()
    {
        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob) || !inJob)
        {
            return "no job";
        }
        var buffer = new byte[144]; // JOBOBJECT_EXTENDED_LIMIT_INFORMATION on x64
        if (!QueryInformationJobObject(IntPtr.Zero, 9, buffer, buffer.Length, out _))
        {
            return "job (flags unreadable)";
        }
        var flags = BitConverter.ToUInt32(buffer, 16);
        return $"job flags=0x{flags:X} killOnClose={(flags & 0x2000) != 0} breakawayOk={(flags & 0x800) != 0}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, byte[] info, int length, out int returned);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? applicationName, System.Text.StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
