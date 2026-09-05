using System.Text.Json;
using System.IO;
using QuotaDock.Providers;

namespace QuotaDock.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuotaDock",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            settings.VerticalOffset = Math.Clamp(settings.VerticalOffset, 0, 1);
            settings.RefreshMinutes = Math.Clamp(settings.RefreshMinutes, 1, 60);
            settings.EnabledProviderIds = ProviderCatalog.Ordered(ProviderCatalog.Normalize(settings.EnabledProviderIds)).ToList();
            return settings;
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
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Position persistence is best-effort; quota polling continues without it.
        }
    }
}
