using System.IO;
using System.Text.Json;
using Hoardling.Core;

namespace Hoardling.Services;

/// <remarks>"Start with Windows" lives only in the registry (single source of truth).</remarks>
public sealed record PetSettings(double? Left, double? Top, bool HooksDisabled)
{
    public static readonly PetSettings Default = new(null, null, false);
}

/// <summary>Persists <see cref="PetSettings"/> as JSON; any problem falls back to defaults.</summary>
public sealed class SettingsStore(string path)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hoardling", "settings.json");

    public PetSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(path)) ?? PetSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return PetSettings.Default;
        }
    }

    public void Save(PetSettings settings)
    {
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the window position is harmless.
        }
    }
}
