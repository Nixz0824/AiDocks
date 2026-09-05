using System.IO;

namespace LineDock.Services;

internal static class CrashLog
{
    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LineDock");

    public static void WriteLine(string message)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(
                Path.Combine(DirectoryPath, "error.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }

    public static void Write(Exception exception) => WriteLine(exception.ToString());
}
