using System.Diagnostics;

namespace Hoardling.Core;

public static class ProcessCheck
{
    /// <summary>True if any process with one of these names (no ".exe") is running. Releases every handle.</summary>
    public static bool AnyRunning(params string[] names)
    {
        foreach (var name in names)
        {
            var processes = Process.GetProcessesByName(name);
            var found = processes.Length > 0;
            foreach (var p in processes)
            {
                p.Dispose();
            }
            if (found)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True if a process whose name starts with <paramref name="namePrefix"/> (case-insensitive) is running
    /// and, when <paramref name="pathFragment"/> is given, its executable path contains it. Releases every handle.
    /// </summary>
    public static bool AnyRunningPrefix(string namePrefix, string? pathFragment)
    {
        var found = false;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!found && p.ProcessName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    found = pathFragment is null
                        || p.MainModule?.FileName.Contains(pathFragment, StringComparison.OrdinalIgnoreCase) == true;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Exited meanwhile or not accessible: not ours.
            }
            finally
            {
                p.Dispose();
            }
        }
        return found;
    }

    /// <summary>True if a process with this name has a visible top-level window (the real app, not a helper).</summary>
    public static bool AnyRunningWithWindow(string name)
    {
        var processes = Process.GetProcessesByName(name);
        var found = false;
        foreach (var p in processes)
        {
            try
            {
                found |= p.MainWindowHandle != IntPtr.Zero;
            }
            catch (InvalidOperationException)
            {
                // Exited meanwhile.
            }
            finally
            {
                p.Dispose();
            }
        }
        return found;
    }

}
