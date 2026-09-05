using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using QuotaDock.Models;

namespace QuotaDock.Providers;

public sealed class CodexQuotaProvider(HttpClient httpClient) : IQuotaProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private readonly CodexAppServerClient _appServer = new();

    public string Id => "codex";
    public string DisplayName => "Codex Usage";
    public string Glyph => "⌬";
    public string AccentHex => "#20E3A2";

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var authPath = ResolveAuthPath();
        if (authPath is null || !File.Exists(authPath))
        {
            return await TryAppServerAsync(cancellationToken) ??
                   ProviderResult.Missing(this, "请先登录 Codex Desktop 或 CLI");
        }

        CodexCredentials? credentials;
        try
        {
            credentials = ParseCredentials(await File.ReadAllTextAsync(authPath, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult.Unavailable(this, "Codex 登录文件无法读取");
        }

        if (credentials is null)
        {
            return ProviderResult.Missing(this, "Codex 登录信息格式不受支持");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.UserAgent.ParseAdd("codex-cli");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return await TryAppServerAsync(cancellationToken) ??
                       ProviderResult.AuthenticationRequired(this, "请重新登录 Codex");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult.Unavailable(this, $"Codex 返回 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var windows = await ParseUsageAsync(stream, cancellationToken);
            return windows.Count == 0
                ? ProviderResult.Unavailable(this, "Codex 未返回可用额度窗口")
                : new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, windows, DateTimeOffset.Now, ProviderState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Unavailable(this, "Codex 请求超时");
        }
        catch (HttpRequestException)
        {
            return ProviderResult.Unavailable(this, "Codex 网络不可用");
        }
        catch (Exception)
        {
            return ProviderResult.Unavailable(this, "Codex 暂时无法读取");
        }
    }

    private async Task<QuotaSnapshot?> TryAppServerAsync(CancellationToken cancellationToken)
    {
        var windows = await _appServer.TryReadAsync(cancellationToken);
        return windows is { Count: > 0 }
            ? new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, windows, DateTimeOffset.Now, ProviderState.Ready)
            : null;
    }

    internal static CodexCredentials? ParseCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!tokens.TryGetProperty("access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            return null;
        }

        var accountId = tokens.TryGetProperty("account_id", out var account) ? account.GetString() : null;
        return new CodexCredentials(accessToken.GetString()!, accountId);
    }

    internal static async Task<IReadOnlyList<QuotaWindow>> ParseUsageAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("rate_limit", out var rateLimit) || rateLimit.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var result = new List<QuotaWindow>(2);
            AddWindow(rateLimit, "primary_window", "5 小时限额", result);
            AddWindow(rateLimit, "secondary_window", "周限额", result);
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddWindow(JsonElement rateLimit, string property, string fallback, ICollection<QuotaWindow> target)
    {
        if (!rateLimit.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!window.TryGetProperty("used_percent", out var percentElement) || !percentElement.TryGetDouble(out var percent))
        {
            return;
        }

        TimeSpan? duration = null;
        if (window.TryGetProperty("limit_window_seconds", out var durationElement) && durationElement.TryGetInt64(out var seconds) && seconds > 0)
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("reset_at", out var resetElement) && resetElement.TryGetInt64(out var unix) && unix > 0)
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        target.Add(new QuotaWindow(ProviderParsing.WindowLabel(duration, fallback), Math.Clamp(percent, 0, 100), resetAt, duration));
    }

    private static string? ResolveAuthPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(Path.Combine(configured, "auth.json"));
        }

        candidates.Add(Path.Combine(home, ".codex", "auth.json"));
        candidates.Add(Path.Combine(appData, "Codex", "auth.json"));
        return candidates.FirstOrDefault(File.Exists);
    }

    internal sealed record CodexCredentials(string AccessToken, string? AccountId);
}
