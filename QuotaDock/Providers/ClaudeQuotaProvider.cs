using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using QuotaDock.Models;

namespace QuotaDock.Providers;

public sealed class ClaudeQuotaProvider(HttpClient httpClient) : IQuotaProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string RefreshUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public string Id => "claude";
    public string DisplayName => "Claude Usage";
    public string Glyph => "✶";
    public string AccentHex => "#D97757";

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        ClaudeCredentials? credentials;
        try
        {
            credentials = ReadCredentials();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult.Unavailable(this, "Claude 登录文件无法读取");
        }

        if (credentials is null)
        {
            return ProviderResult.Missing(this, "请先登录 Claude Desktop 或 Claude Code");
        }

        var token = credentials.AccessToken;
        if (credentials.IsExpired && !string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            token = await TryRefreshAsync(credentials.RefreshToken, cancellationToken) ?? token;
        }

        return await FetchWithTokenAsync(token, credentials.RefreshToken, true, cancellationToken);
    }

    private async Task<QuotaSnapshot> FetchWithTokenAsync(string token, string? refreshToken, bool allowRefresh, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("claude-cli/2.1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (allowRefresh && !string.IsNullOrWhiteSpace(refreshToken))
                {
                    var refreshed = await TryRefreshAsync(refreshToken, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(refreshed))
                    {
                        return await FetchWithTokenAsync(refreshed, refreshToken, false, cancellationToken);
                    }
                }

                return ProviderResult.AuthenticationRequired(this, "请重新登录 Claude");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult.Unavailable(this, $"Claude 返回 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var windows = await ParseUsageAsync(stream, cancellationToken);
            return windows.Count == 0
                ? ProviderResult.Unavailable(this, "Claude 未返回可用额度窗口")
                : new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, windows, DateTimeOffset.Now, ProviderState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Unavailable(this, "Claude 请求超时");
        }
        catch (HttpRequestException)
        {
            return ProviderResult.Unavailable(this, "Claude 网络不可用");
        }
        catch (Exception)
        {
            return ProviderResult.Unavailable(this, "Claude 暂时无法读取");
        }
    }

    internal static ClaudeCredentials? ParseCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var access = oauth.TryGetProperty("accessToken", out var accessToken) ? accessToken.GetString() : null;
        if (string.IsNullOrWhiteSpace(access))
        {
            return null;
        }

        var refresh = oauth.TryGetProperty("refreshToken", out var refreshToken) ? refreshToken.GetString() : null;
        long? expiresAt = null;
        if (oauth.TryGetProperty("expiresAt", out var expires) && expires.TryGetInt64(out var value) && value > 0)
        {
            expiresAt = value;
        }

        return new ClaudeCredentials(access, refresh, expiresAt);
    }

    internal static async Task<IReadOnlyList<QuotaWindow>> ParseUsageAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var result = new List<QuotaWindow>(2);
            AddWindow(document.RootElement, "five_hour", "5 小时限额", TimeSpan.FromHours(5), result);
            AddWindow(document.RootElement, "seven_day", "周限额", TimeSpan.FromDays(7), result);
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddWindow(JsonElement root, string property, string label, TimeSpan duration, ICollection<QuotaWindow> target)
    {
        if (!root.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!window.TryGetProperty("utilization", out var utilization) || !utilization.TryGetDouble(out var percent))
        {
            return;
        }

        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resets_at", out var reset) && DateTimeOffset.TryParse(reset.GetString(), out var parsed))
        {
            resetAt = parsed;
        }

        target.Add(new QuotaWindow(label, Math.Clamp(percent, 0, 100), resetAt, duration));
    }

    private static ClaudeCredentials? ReadCredentials()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new ClaudeCredentials(env.Trim(), null, null);
        }

        var path = ResolveCredentialsPath();
        return path is null || !File.Exists(path) ? null : ParseCredentials(File.ReadAllText(path));
    }

    private static string? ResolveCredentialsPath()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var home = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".credentials.json");
    }

    private async Task<string?> TryRefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl) { Content = content };
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    internal sealed record ClaudeCredentials(string AccessToken, string? RefreshToken, long? ExpiresAt)
    {
        public bool IsExpired
        {
            get
            {
                if (ExpiresAt is null)
                {
                    return false;
                }

                var milliseconds = ExpiresAt.Value > 10_000_000_000 ? ExpiresAt.Value : ExpiresAt.Value * 1000;
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) <= DateTimeOffset.UtcNow.AddMinutes(2);
            }
        }
    }
}
