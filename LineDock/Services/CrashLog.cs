using System.IO;
using System.Text.RegularExpressions;

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
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {Redact(message)}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }

    public static void Write(Exception exception) => WriteLine(exception.ToString());

    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = Regex.Replace(text, @"Bearer\s+[A-Za-z0-9._\-+=/]+", "Bearer [redacted]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(access_token|refresh_token|id_token|api[_-]?key|client_secret)\s*[:=]\s*[^\s,;""]+", "$1=[redacted]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"sk-[A-Za-z0-9]{8,}", "sk-[redacted]");
        text = Regex.Replace(text, @"GOCSPX-[A-Za-z0-9_\-]+", "[redacted]");
        text = Regex.Replace(text, @"eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", "[redacted-jwt]");
        text = Regex.Replace(text, @"gh[pousr]_[A-Za-z0-9]{20,}", "[redacted]");
        return text;
    }
}
