using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TwinDock.Services;

internal enum UpdateKind
{
    Current,
    Available,
    Unreachable
}

internal sealed record UpdateResult(UpdateKind Kind, Version Current, Version? Latest, string? Url)
{
    public string Message => Kind switch
    {
        UpdateKind.Available => $"有新版本  {Latest}",
        UpdateKind.Current => $"已是最新  {Current}",
        _ => $"当前 {Current} · 稍后重试"
    };
}

internal static class AppUpdate
{
    public const string AppId = "twindock";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? new Version(0, 0, 0) : new Version(version.Major, version.Minor, version.Build);
        }
    }

    public static async Task<UpdateResult> CheckAsync(CancellationToken cancellationToken)
    {
        var current = CurrentVersion;
        Version? latest = null;
        string? url = null;
        foreach (var source in Sources())
        {
            var doc = await ReadAsync(source, cancellationToken);
            if (doc is null || !Version.TryParse(doc.Version, out var parsed))
            {
                continue;
            }

            if (latest is null || parsed > latest)
            {
                latest = parsed;
                url = string.IsNullOrWhiteSpace(doc.Url) ? null : doc.Url;
            }
        }

        if (latest is null)
        {
            return new UpdateResult(UpdateKind.Unreachable, current, null, null);
        }

        return latest > current
            ? new UpdateResult(UpdateKind.Available, current, latest, url)
            : new UpdateResult(UpdateKind.Current, current, latest, url);
    }

    public static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the download is best-effort.
        }
    }

    private static IEnumerable<string> Sources()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "latest.json");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiDocks",
            "latest.json");
        yield return "https://raw.githubusercontent.com/aidock-feed/releases/main/latest.json";
    }

    private static async Task<FeedEntry?> ReadAsync(string source, CancellationToken cancellationToken)
    {
        try
        {
            string text;
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                text = await http.GetStringAsync(source, cancellationToken);
            }
            else
            {
                if (!File.Exists(source))
                {
                    return null;
                }

                text = await File.ReadAllTextAsync(source, cancellationToken);
            }

            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty(AppId, out var node))
            {
                return null;
            }

            return JsonSerializer.Deserialize<FeedEntry>(node.GetRawText(), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed class FeedEntry
    {
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
