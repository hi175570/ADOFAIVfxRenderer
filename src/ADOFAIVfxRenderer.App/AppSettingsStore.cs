using System.IO;
using System.Text.Json;

namespace ADOFAIVfxRenderer.App;

internal sealed record AppSettings(
    string GameDirectory = "",
    string LastChartPath = "",
    string LastChartDirectory = "",
    bool LimitMemory = true,
    int MemoryLimitMb = 4096);

internal static class AppSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADOFAIVfxRenderer");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, SettingsPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Remembering paths is a convenience; it must never block rendering or shutdown.
        }
    }
}

internal static class PreRenderCacheRegistry
{
    private static readonly string RegistryDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADOFAIVfxRenderer");

    private static readonly string RegistryPath = Path.Combine(RegistryDirectory, "pre-render-cache-roots.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(RegistryPath)
                ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(RegistryPath)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static void Register(string path)
    {
        var roots = Load().Append(Path.GetFullPath(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Save(roots);
    }

    public static void Clear() => Save([]);

    private static void Save(IReadOnlyCollection<string> roots)
    {
        try
        {
            Directory.CreateDirectory(RegistryDirectory);
            File.WriteAllText(RegistryPath, JsonSerializer.Serialize(roots));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache cleanup remains available for the current chart even if the registry cannot be saved.
        }
    }
}
