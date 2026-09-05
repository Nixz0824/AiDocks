using System.IO;

namespace QuotaDock.Services;

internal static class CrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuotaDock");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {exception}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
