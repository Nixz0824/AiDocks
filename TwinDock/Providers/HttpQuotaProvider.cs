using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TwinDock.Models;

namespace TwinDock.Providers;

internal sealed record HttpQuotaSpec(
    string Id,
    string DisplayName,
    string Glyph,
    string AccentHex,
    string LoginHint,
    string[] EnvKeys,
    string[] FileHints,
    string[] Urls,
    bool Post = false,
    string? Body = null,
    bool Bearer = true);

internal static class LocalCredential
{
    public static string? ReadToken(IEnumerable<string> envKeys, IEnumerable<string> fileHints)
    {
        foreach (var key in envKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        foreach (var path in Expand(fileHints))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var token = Extract(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    internal static IEnumerable<string> Expand(IEnumerable<string> hints)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var hint in hints)
        {
            yield return hint
                .Replace("{Home}", home, StringComparison.OrdinalIgnoreCase)
                .Replace("{AppData}", app, StringComparison.OrdinalIgnoreCase)
                .Replace("{LocalAppData}", local, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string? Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            var toml = Regex.Match(trimmed, @"api_key\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (toml.Success)
            {
                return toml.Groups[1].Value.Trim();
            }

            return trimmed.Contains('\n') ? null : trimmed;
        }

        using var document = JsonDocument.Parse(trimmed);
        return ExtractElement(document.RootElement);
    }

    private static string? ExtractElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ExtractElement(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            return null;
        }

        foreach (var name in new[]
                 {
                     "accessToken", "access_token", "api_key", "apiKey", "token", "key",
                     "authToken", "CODEBUDDY_AUTH_TOKEN"
                 })
        {
            if (element.TryGetProperty(name, out var node))
            {
                var value = ExtractElement(node);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        if (element.TryGetProperty("auth", out var auth))
        {
            var nested = ExtractElement(auth);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            var nested = ExtractElement(property.Value);
            if (!string.IsNullOrWhiteSpace(nested) && nested.Length >= 16)
            {
                return nested;
            }
        }

        return null;
    }
}

internal sealed class HttpQuotaProvider(HttpClient httpClient, HttpQuotaSpec spec) : IQuotaProvider
{
    public string Id => spec.Id;
    public string DisplayName => spec.DisplayName;
    public string Glyph => spec.Glyph;
    public string AccentHex => spec.AccentHex;

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? token;
        try
        {
            token = LocalCredential.ReadToken(spec.EnvKeys, spec.FileHints);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult.Unavailable(this, $"{ShortName()} 登录文件无法读取");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ProviderResult.Missing(this, spec.LoginHint);
        }

        Exception? lastNetwork = null;
        foreach (var url in spec.Urls)
        {
            try
            {
                using var request = new HttpRequestMessage(spec.Post ? HttpMethod.Post : HttpMethod.Get, url);
                if (spec.Bearer)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation("Authorization", token);
                }

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("User-Agent", "AiDocks/0.3");
                if (spec.Post)
                {
                    request.Content = new StringContent(spec.Body ?? "{}", Encoding.UTF8, "application/json");
                }

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return ProviderResult.AuthenticationRequired(this, $"请重新登录 {ShortName()}");
                }

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var windows = JsonUsage.Parse(document.RootElement);
                if (windows.Count > 0)
                {
                    return new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, windows, DateTimeOffset.Now, ProviderState.Ready);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return ProviderResult.Unavailable(this, $"{ShortName()} 请求超时");
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
            {
                lastNetwork = exception;
            }
        }

        return lastNetwork is HttpRequestException
            ? ProviderResult.Unavailable(this, $"{ShortName()} 网络不可用")
            : ProviderResult.Unavailable(this, $"{ShortName()} 未返回可用额度");
    }

    private string ShortName() => DisplayName.Replace(" Usage", "", StringComparison.Ordinal);
}

internal static class JsonUsage
{
    public static IReadOnlyList<QuotaWindow> Parse(JsonElement root)
    {
        var result = new List<QuotaWindow>();
        Walk(root, result);
        if (result.Count > 0)
        {
            return result
                .GroupBy(window => window.Label)
                .Select(group => group.First())
                .Take(3)
                .ToArray();
        }

        if (TryBalance(root, out var remaining) && remaining >= 0)
        {
            var used = remaining <= 0 ? 100 : 0;
            return [new QuotaWindow("账户余额", used, null, null)];
        }

        return [];
    }

    private static void Walk(JsonElement element, List<QuotaWindow> target)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Walk(item, target);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        TryAddObject(element, target);
        foreach (var property in element.EnumerateObject())
        {
            Walk(property.Value, target);
        }
    }

    private static void TryAddObject(JsonElement element, List<QuotaWindow> target)
    {
        var label = LabelOf(element);
        double? percent = ReadPercent(element);
        if (percent is null)
        {
            if (TryPair(element, out var used, out var total) && total > 0)
            {
                percent = used / total * 100;
            }
            else if (TryPairRemaining(element, out var remaining, out var cap) && cap > 0)
            {
                percent = (1 - remaining / cap) * 100;
            }
        }

        if (percent is null)
        {
            return;
        }

        DateTimeOffset? reset = ReadReset(element);
        TimeSpan? duration = label.Contains("5") ? TimeSpan.FromHours(5)
            : label.Contains("日") ? TimeSpan.FromHours(24)
            : label.Contains("周") ? TimeSpan.FromDays(7)
            : TimeSpan.FromDays(30);
        target.Add(new QuotaWindow(label, Math.Clamp(percent.Value, 0, 100), reset, duration));
    }

    private static string LabelOf(JsonElement element)
    {
        var blob = $"{ReadString(element, "type", "label", "name", "window", "period")} {ReadString(element, "id")}";
        if (blob.Contains("5h", StringComparison.OrdinalIgnoreCase) ||
            blob.Contains("five", StringComparison.OrdinalIgnoreCase) ||
            blob.Contains("rolling", StringComparison.OrdinalIgnoreCase) ||
            blob.Contains("hour", StringComparison.OrdinalIgnoreCase))
        {
            return "5 小时限额";
        }

        if (blob.Contains("week", StringComparison.OrdinalIgnoreCase) || blob.Contains("7d", StringComparison.OrdinalIgnoreCase))
        {
            return "周限额";
        }

        if (blob.Contains("day", StringComparison.OrdinalIgnoreCase) || blob.Contains("daily", StringComparison.OrdinalIgnoreCase))
        {
            return "每日限额";
        }

        if (blob.Contains("month", StringComparison.OrdinalIgnoreCase) || blob.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
            blob.Contains("credit", StringComparison.OrdinalIgnoreCase) || blob.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return "订阅周期";
        }

        return "订阅周期";
    }

    private static double? ReadPercent(JsonElement element)
    {
        foreach (var name in new[] { "percentage", "percent", "usedPercent", "usagePercent", "used_percent", "usage_percent", "totalUsagePercentage" })
        {
            if (element.TryGetProperty(name, out var node) && node.TryGetDouble(out var value))
            {
                return value > 1 && value <= 100 ? value : value <= 1 ? value * 100 : value;
            }
        }

        return null;
    }

    private static bool TryPair(JsonElement element, out double used, out double total)
    {
        used = ReadNumber(element, "used", "usage", "currentValue", "consumed", "usedCredits") ?? -1;
        total = ReadNumber(element, "total", "limit", "quota", "cap", "max", "entitlement") ?? -1;
        return used >= 0 && total > 0;
    }

    private static bool TryPairRemaining(JsonElement element, out double remaining, out double total)
    {
        remaining = ReadNumber(element, "remaining", "remain", "left", "available") ?? -1;
        total = ReadNumber(element, "total", "limit", "quota", "cap") ?? -1;
        return remaining >= 0 && total > 0;
    }

    private static bool TryBalance(JsonElement element, out double remaining)
    {
        remaining = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("data", out var data))
        {
            remaining = ReadNumber(data, "available_balance", "availableBalance", "balance") ?? 0;
            return data.TryGetProperty("available_balance", out _) || data.TryGetProperty("availableBalance", out _) ||
                   data.TryGetProperty("balance", out _);
        }

        remaining = ReadNumber(element, "available_balance", "balance") ?? 0;
        return element.TryGetProperty("available_balance", out _) || element.TryGetProperty("balance", out _);
    }

    private static DateTimeOffset? ReadReset(JsonElement element)
    {
        foreach (var name in new[] { "resetsAt", "resetAt", "reset_at", "nextResetTime", "expire", "expiresAt" })
        {
            if (!element.TryGetProperty(name, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var unix))
            {
                if (unix > 1_000_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(unix);
                }

                if (unix > 1_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unix);
                }
            }

            if (node.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(node.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? ReadNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var node) && node.TryGetDouble(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String)
            {
                return node.GetString() ?? "";
            }
        }

        return "";
    }
}

