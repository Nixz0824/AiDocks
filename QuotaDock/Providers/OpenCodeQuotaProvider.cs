using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using QuotaDock.Models;

namespace QuotaDock.Providers;

/// <summary>
/// OpenCode Go 订阅额度：官方接口 GET https://opencode.ai/zen/go/v1/usage。
/// 合并 PR sst/opencode#16513，返回 rollingUsage / weeklyUsage / monthlyUsage
/// 三个窗口的 usagePercent + resetInSec。Zen 按量余额没有官方 API，不捏造。
/// </summary>
public sealed class OpenCodeQuotaProvider(HttpClient httpClient) : IQuotaProvider
{
    internal const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";

    public string Id => "opencode";
    public string DisplayName => "OpenCode Usage";
    public string Glyph => "▣";
    public string AccentHex => "#E4E4E4";

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? apiKey;
        try
        {
            apiKey = ResolveApiKey();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult.Unavailable(this, "OpenCode 登录文件无法读取");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderResult.Missing(this, "请先运行 opencode auth login 选择 OpenCode Go，或在 opencode.ai/auth 复制 API Key");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("QuotaDock/0.5.3");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                return ProviderResult.AuthenticationRequired(this, "OpenCode API Key 无效，请重新登录");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden)
            {
                return ProviderResult.Unavailable(this, "该 Key 没有 OpenCode Go 订阅");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult.Unavailable(this, $"OpenCode 返回 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var windows = await ParseUsageAsync(stream, cancellationToken);
            return windows.Count == 0
                ? ProviderResult.Unavailable(this, "OpenCode 未返回可用额度窗口")
                : new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, windows, DateTimeOffset.Now, ProviderState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Unavailable(this, "OpenCode 请求超时");
        }
        catch (HttpRequestException)
        {
            return ProviderResult.Unavailable(this, "OpenCode 网络不可用");
        }
        catch (Exception)
        {
            return ProviderResult.Unavailable(this, "OpenCode 暂时无法读取");
        }
    }

    internal static async Task<IReadOnlyList<QuotaWindow>> ParseUsageAsync(Stream stream, CancellationToken cancellationToken, DateTimeOffset? now = null)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var timestamp = now ?? DateTimeOffset.Now;
            var result = new List<QuotaWindow>(3);
            AddWindow(root, "rollingUsage", "5 小时滚动", TimeSpan.FromHours(5), timestamp, result);
            AddWindow(root, "weeklyUsage", "周限额", TimeSpan.FromDays(7), timestamp, result);
            AddWindow(root, "monthlyUsage", "月限额", TimeSpan.FromDays(30), timestamp, result);
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddWindow(JsonElement root, string property, string label, TimeSpan duration, DateTimeOffset now, ICollection<QuotaWindow> target)
    {
        if (!TryGetPropertyIgnoreCase(root, property, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!TryReadDouble(window, "usagePercent", out var percent) && !TryReadDouble(window, "usage_percent", out percent))
        {
            return;
        }

        DateTimeOffset? resetAt = null;
        if ((TryReadDouble(window, "resetInSec", out var resetSec) || TryReadDouble(window, "reset_in_seconds", out resetSec) || TryReadDouble(window, "reset_in_sec", out resetSec)) && resetSec > 0)
        {
            resetAt = now.AddSeconds(resetSec);
        }

        target.Add(new QuotaWindow(label, Math.Clamp(percent, 0, 100), resetAt, duration));
    }

    internal static string? SelectApiKey(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // opencode auth login -p opencode-go 写的是严格的 { "type": "api", "key": "..." }，
        // 兼容旧的 opencode 条目与大小写差异。
        foreach (var name in new[] { "opencode-go", "opencode" })
        {
            if (TryGetPropertyIgnoreCase(document.RootElement, name, out var entry))
            {
                var key = ExtractKey(entry);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key;
                }
            }
        }

        return null;
    }

    internal static string? SelectApiKeyFromConfig(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!TryGetPropertyIgnoreCase(document.RootElement, "provider", out var provider) || provider.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "opencode-go", "opencode" })
            {
                if (!TryGetPropertyIgnoreCase(provider, name, out var entry) || entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryGetPropertyIgnoreCase(entry, "options", out var options) && options.ValueKind == JsonValueKind.Object)
                {
                    foreach (var keyName in new[] { "apiKey", "api_key", "key" })
                    {
                        if (TryGetPropertyIgnoreCase(options, keyName, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            var key = value.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(key))
                            {
                                return key;
                            }
                        }
                    }
                }
            }

            return null;
        }
    }

    internal static string? ResolveApiKey()
    {
        foreach (var name in new[] { "OPENCODE_GO_API_KEY", "OPENCODE_API_KEY" })
        {
            var env = Environment.GetEnvironmentVariable(name)?.Trim();
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }
        }

        foreach (var path in ConfigPaths())
        {
            try
            {
                if (File.Exists(path))
                {
                    var key = SelectApiKeyFromConfig(File.ReadAllText(path));
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        return key;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Try the next candidate path.
            }
        }

        foreach (var path in AuthPaths())
        {
            try
            {
                if (File.Exists(path))
                {
                    var key = SelectApiKey(File.ReadAllText(path));
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        return key;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Try the next candidate path.
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> AuthPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>(5);
        // opencode 跨平台（含 Windows）主路径：~/.local/share/opencode/auth.json，见官方 troubleshooting 文档。
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, ".local", "share", "opencode", "auth.json"));
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            candidates.Add(Path.Combine(xdg, "opencode", "auth.json"));
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, ".config", "opencode", "auth.json"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "opencode", "auth.json"));
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, "Library", "Application Support", "opencode", "auth.json"));
        }

        return candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<string> ConfigPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>(4);
        // opencode.json 在全平台（含 Windows）都是 ~/.config/opencode/。
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, ".config", "opencode", "opencode.json"));
            candidates.Add(Path.Combine(home, ".config", "opencode", "opencode.jsonc"));
        }

        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.Combine(appData, "opencode", "opencode.json"));
            candidates.Add(Path.Combine(appData, "opencode", "opencode.jsonc"));
        }

        return candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string? AuthDirectory()
    {
        foreach (var path in AuthPaths())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static string? ExtractKey(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            var direct = entry.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(direct) ? null : direct;
        }

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var keyName in new[] { "key", "apiKey", "api_key" })
        {
            if (TryGetPropertyIgnoreCase(entry, keyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var key = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key;
                }
            }
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!TryGetPropertyIgnoreCase(element, name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(property.GetString(), out value),
            _ => false
        };
    }
}
