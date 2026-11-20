using System.IO;

namespace Hoardling.Services;

/// <summary>
/// Hoardling was called DragonPet before its first release. Moves what that name left on this PC: the
/// window position and preferences, and the "start with Windows" entry (it pointed at DragonPet.exe).
/// Old hooks in Claude Code's settings are replaced by <see cref="Core.ClaudeHooksInstaller"/> itself.
/// Best effort: any problem leaves the defaults, never a crash.
/// </summary>
internal static class LegacyMigration
{
    private const string OldName = "DragonPet";

    public static void Run(string settingsPath, string? exePath)
    {
        MoveSettings(settingsPath);
        // Only a real Hoardling.exe may go in the Run key (not dotnet.exe when run as "dotnet Hoardling.dll").
        if (exePath is not null && Path.GetFileNameWithoutExtension(exePath).Equals("Hoardling", StringComparison.OrdinalIgnoreCase))
        {
            MoveStartupEntry(exePath);
        }
    }

    private static void MoveSettings(string settingsPath)
    {
        var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName);
        var old = Path.Combine(oldDir, "settings.json");
        try
        {
            if (File.Exists(settingsPath) || !File.Exists(old))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.Move(old, settingsPath);
            if (!Directory.EnumerateFileSystemEntries(oldDir).Any())
            {
                Directory.Delete(oldDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Worst case the dragon starts in its default corner.
        }
    }

    private static void MoveStartupEntry(string exePath)
    {
        try
        {
            if (StartupRegistry.RemoveValue(OldName))
            {
                StartupRegistry.Set(true, exePath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Blocked by policy: the user can turn it on again from the tray menu.
        }
    }
}
