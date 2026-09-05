using System.Diagnostics;
using System.IO;
using System.Text.Json;
using QuotaDock.Models;

namespace QuotaDock.Providers;

internal sealed class CodexAppServerClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    public async Task<IReadOnlyList<QuotaWindow>?> TryReadAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null)
        {
            return null;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server --stdio",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        Task<string>? stderrDrain = null;

        try
        {
            if (!process.Start())
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            stderrDrain = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"quota-dock\",\"title\":\"QuotaDock\",\"version\":\"0.3.0\"},\"capabilities\":{\"experimentalApi\":true}}}");
            await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
            await process.StandardInput.FlushAsync(timeout.Token);

            while (!timeout.Token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                var windows = ParseRateLimitResponse(line);
                if (windows is not null)
                {
                    return windows;
                }
            }

            await stderrDrain.WaitAsync(timeout.Token);
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(exitTimeout.Token);
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch
                {
                    // The short-lived helper is already gone.
                }
            }

            if (stderrDrain is not null)
            {
                try
                {
                    await stderrDrain.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch
                {
                    // Closing the helper can truncate diagnostic stderr; it carries no quota payload.
                }
            }
        }
    }

    internal static IReadOnlyList<QuotaWindow>? ParseRateLimitResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != 2 ||
            !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("rateLimits", out var rateLimits) || rateLimits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var windows = new List<QuotaWindow>(2);
        AddWindow(rateLimits, "primary", "5 小时限额", windows);
        AddWindow(rateLimits, "secondary", "周限额", windows);
        return windows;
    }

    private static void AddWindow(JsonElement rateLimits, string property, string fallback, ICollection<QuotaWindow> target)
    {
        if (!rateLimits.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var percentElement) || !percentElement.TryGetDouble(out var usedPercent))
        {
            return;
        }

        TimeSpan? duration = null;
        if (window.TryGetProperty("windowDurationMins", out var durationElement) && durationElement.TryGetInt64(out var minutes) && minutes > 0)
        {
            duration = TimeSpan.FromMinutes(minutes);
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) && resetElement.TryGetInt64(out var unix) && unix > 0)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        target.Add(new QuotaWindow(ProviderParsing.WindowLabel(duration, fallback), Math.Clamp(usedPercent, 0, 100), resetsAt, duration));
    }

    private static string? ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "codex.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        var binRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        try
        {
            return Directory.Exists(binRoot)
                ? Directory.EnumerateFiles(binRoot, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
