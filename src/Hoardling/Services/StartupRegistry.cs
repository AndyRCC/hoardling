using Microsoft.Win32;

namespace Hoardling.Services;

/// <summary>"Start with Windows" through HKCU\...\Run (per user, no admin rights).</summary>
public static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Hoardling";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Deletes a Run value by name. True if it existed.</summary>
    public static bool RemoveValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(valueName) is not string)
        {
            return false;
        }
        key.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
