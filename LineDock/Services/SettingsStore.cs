using System.IO;
using System.Text.Json;

namespace LineDock.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly int[] AllowedIntervals = [5, 10, 15, 30];

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LineDock",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return Normalize(new AppSettings());
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            return Normalize(settings);
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Position persistence is best-effort.
        }
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        settings.VerticalOffset = Math.Clamp(settings.VerticalOffset, 0, 1);
        settings.IntervalSeconds = AllowedIntervals.Contains(settings.IntervalSeconds)
            ? settings.IntervalSeconds
            : 10;
        settings.EnabledTargetIds = ProbeCatalog.Normalize(settings.EnabledTargetIds).ToList();
        return settings;
    }

    public static int NextInterval(int current)
    {
        var index = Array.IndexOf(AllowedIntervals, current);
        return AllowedIntervals[(index + 1) % AllowedIntervals.Length];
    }
}
