using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace QuotaDock.Services;

internal static class StartupLaunch
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuotaDock";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return !string.IsNullOrWhiteSpace(key?.GetValue(ValueName) as string);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, false);
                return;
            }

            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            key.SetValue(ValueName, $"\"{path}\"");
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Startup registration is best-effort.
        }
    }
}
