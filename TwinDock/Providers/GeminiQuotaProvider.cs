using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using TwinDock.Models;

namespace TwinDock.Providers;

public sealed class GeminiQuotaProvider(HttpClient httpClient) : IQuotaProvider
{
    private const string LoadUrl = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string QuotaUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const string RefreshUrl = "https://oauth2.googleapis.com/token";

    public string Id => "gemini";
    public string DisplayName => "Gemini Usage";
    public string Glyph => "✦";
    public string AccentHex => "#8AB4F8";

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        GeminiCredentials? credentials;
        try
        {
            credentials = ReadCredentials();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult.Unavailable(this, "Gemini 登录文件无法读取");
        }

        if (credentials is null)
        {
            return ProviderResult.Missing(this, "请先登录 Gemini CLI（gemini），或登录 Google AI Studio");
        }

        var token = credentials.AccessToken;
        if (!string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            token = await TryRefreshAsync(credentials, cancellationToken) ?? token;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ProviderResult.AuthenticationRequired(this, "请重新登录 Gemini CLI");
        }

        try
        {
            var project = await LoadProjectAsync(token, cancellationToken);
            if (string.IsNullOrWhiteSpace(project))
            {
                return ProviderResult.Unavailable(this, "Gemini 未返回可用项目");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, QuotaUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { project }), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ProviderResult.AuthenticationRequired(this, "请重新登录 Gemini CLI");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult.Unavailable(this, $"Gemini 返回 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var windows = await ParseUsageAsync(stream, cancellationToken);
            return windows.Count == 0
                ? ProviderResult.Unavailable(this, "Gemini 未返回可用额度窗口")
                : new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, windows, DateTimeOffset.Now, ProviderState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Unavailable(this, "Gemini 请求超时");
        }
        catch (HttpRequestException)
        {
            return ProviderResult.Unavailable(this, "Gemini 网络不可用");
        }
        catch (Exception)
        {
            return ProviderResult.Unavailable(this, "Gemini 暂时无法读取");
        }
    }

    internal static GeminiCredentials? ParseCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var access = root.TryGetProperty("access_token", out var accessToken) ? accessToken.GetString() : null;
        var refresh = root.TryGetProperty("refresh_token", out var refreshToken) ? refreshToken.GetString() : null;
        var clientId = root.TryGetProperty("client_id", out var clientIdEl) ? clientIdEl.GetString() : null;
        var clientSecret = root.TryGetProperty("client_secret", out var clientSecretEl) ? clientSecretEl.GetString() : null;
        return string.IsNullOrWhiteSpace(access) && string.IsNullOrWhiteSpace(refresh)
            ? null
            : new GeminiCredentials(access, refresh, clientId, clientSecret);
    }

    internal static async Task<IReadOnlyList<QuotaWindow>> ParseUsageAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            double? minRemaining = null;
            DateTimeOffset? resetAt = null;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.TryGetProperty("remainingFraction", out var fraction) && fraction.TryGetDouble(out var remaining))
                {
                    minRemaining = minRemaining is null ? remaining : Math.Min(minRemaining.Value, remaining);
                }

                if (resetAt is null && bucket.TryGetProperty("resetTime", out var reset) && DateTimeOffset.TryParse(reset.GetString(), out var parsed))
                {
                    resetAt = parsed;
                }
            }

            if (minRemaining is null)
            {
                return [];
            }

            var used = Math.Clamp((1 - minRemaining.Value) * 100, 0, 100);
            return [new QuotaWindow("每日限额", used, resetAt, TimeSpan.FromHours(24))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static GeminiCredentials? ReadCredentials()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");
        return File.Exists(path) ? ParseCredentials(File.ReadAllText(path)) : null;
    }

    private async Task<string?> LoadProjectAsync(string token, CancellationToken cancellationToken)
    {
        var body = """{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, LoadUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("cloudaicompanionProject", out var project)
            ? project.GetString()
            : null;
    }

    private async Task<string?> TryRefreshAsync(GeminiCredentials credentials, CancellationToken cancellationToken)
    {
        var refreshToken = credentials.RefreshToken;
        var clientId = FirstNonEmpty(credentials.ClientId, Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_ID"));
        var clientSecret = FirstNonEmpty(credentials.ClientSecret, Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_SECRET"));
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
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

    internal sealed record GeminiCredentials(string? AccessToken, string? RefreshToken, string? ClientId = null, string? ClientSecret = null);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
