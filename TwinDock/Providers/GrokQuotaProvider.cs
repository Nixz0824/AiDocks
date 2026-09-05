using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using TwinDock.Models;

namespace TwinDock.Providers;

public sealed class GrokQuotaProvider(HttpClient httpClient) : IQuotaProvider
{
    private const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    private readonly GrokCliCredentialRefresher _credentialRefresher = new();

    public string Id => "grok";
    public string DisplayName => "Grok Usage";
    public string Glyph => "×";
    public string AccentHex => "#FF5A36";

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var authPath = ResolveAuthPath();
        if (authPath is null || !File.Exists(authPath))
        {
            return ProviderResult.Missing(this, "请先运行 grok login");
        }

        string? token;
        try
        {
            token = SelectXaiToken(await File.ReadAllTextAsync(authPath, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult.Unavailable(this, "Grok 登录文件无法读取");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ProviderResult.Missing(this, "未找到 xAI 签发的 Grok CLI 登录");
        }

        return await FetchWithTokenAsync(token, authPath, true, cancellationToken);
    }

    private async Task<QuotaSnapshot> FetchWithTokenAsync(string token, string authPath, bool allowRefresh, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BillingUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("TwinDock/0.3");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (allowRefresh && await _credentialRefresher.TryRefreshAsync(cancellationToken))
                {
                    try
                    {
                        var refreshedToken = SelectXaiToken(await File.ReadAllTextAsync(authPath, cancellationToken));
                        if (!string.IsNullOrWhiteSpace(refreshedToken))
                        {
                            return await FetchWithTokenAsync(refreshedToken, authPath, false, cancellationToken);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                    {
                        // Fall through to the explicit login-required state.
                    }
                }

                return ProviderResult.AuthenticationRequired(this, "请重新运行 grok login");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult.Unavailable(this, $"Grok 返回 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var windows = await ParseUsageAsync(stream, cancellationToken);
            return windows.Count == 0
                ? ProviderResult.Unavailable(this, "Grok 未返回可用订阅周期")
                : new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, windows, DateTimeOffset.Now, ProviderState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Unavailable(this, "Grok 请求超时");
        }
        catch (HttpRequestException)
        {
            return ProviderResult.Unavailable(this, "Grok 网络不可用");
        }
        catch (Exception)
        {
            return ProviderResult.Unavailable(this, "Grok 暂时无法读取");
        }
    }

    internal static string? SelectXaiToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entries = document.RootElement.EnumerateObject()
            .Where(entry => entry.Value.ValueKind == JsonValueKind.Object)
            .Select(entry => new
            {
                entry.Name,
                Value = entry.Value,
                Issuer = entry.Value.TryGetProperty("oidc_issuer", out var issuer) ? issuer.GetString() : entry.Name.Split("::")[0],
                Token = entry.Value.TryGetProperty("key", out var key) ? key.GetString()?.Trim() : null
            })
            .Where(entry => IsXaiIssuer(entry.Issuer) && !string.IsNullOrWhiteSpace(entry.Token))
            .OrderByDescending(entry => entry.Name.StartsWith("https://auth.x.ai::", StringComparison.OrdinalIgnoreCase));

        return entries.FirstOrDefault()?.Token;
    }

    internal static async Task<IReadOnlyList<QuotaWindow>> ParseUsageAsync(Stream stream, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return [];
        }

        using var parsed = document;
        if (!parsed.RootElement.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("currentPeriod", out var period) || period.ValueKind != JsonValueKind.Object ||
            !period.TryGetProperty("end", out var endElement) || !DateTimeOffset.TryParse(endElement.GetString(), out var end))
        {
            return [];
        }

        DateTimeOffset? start = null;
        if (period.TryGetProperty("start", out var startElement) && DateTimeOffset.TryParse(startElement.GetString(), out var parsedStart))
        {
            start = parsedStart;
        }

        double percent;
        if (config.TryGetProperty("creditUsagePercent", out var percentElement) && percentElement.TryGetDouble(out var providerPercent))
        {
            percent = providerPercent;
        }
        else if (TryReadAmount(config, "onDemandUsed", out var used) && TryReadAmount(config, "onDemandCap", out var cap) && cap > 0)
        {
            percent = used / cap * 100;
        }
        else
        {
            percent = 0;
        }

        TimeSpan? duration = start is null ? null : end - start.Value;
        var label = period.TryGetProperty("type", out var type)
            ? PeriodLabel(type.GetString(), duration)
            : ProviderParsing.WindowLabel(duration, "订阅周期");
        return [new QuotaWindow(label, Math.Clamp(percent, 0, 100), end, duration)];
    }

    private static bool TryReadAmount(JsonElement config, string property, out double value)
    {
        value = 0;
        return config.TryGetProperty(property, out var amount) && amount.ValueKind == JsonValueKind.Object &&
               amount.TryGetProperty("val", out var val) && val.TryGetDouble(out value);
    }

    private static string PeriodLabel(string? type, TimeSpan? duration)
    {
        var normalized = type?.Replace("USAGE_PERIOD_TYPE_", string.Empty, StringComparison.OrdinalIgnoreCase);
        return normalized?.ToUpperInvariant() switch
        {
            "WEEKLY" => "每周额度",
            "MONTHLY" => "订阅周期",
            "DAILY" => "每日额度",
            _ => ProviderParsing.WindowLabel(duration, "订阅周期")
        };
    }

    private static bool IsXaiIssuer(string? issuer)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return uri.Host.Equals("x.ai", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveAuthPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("GROK_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(Path.Combine(configured, "auth.json"));
        }

        candidates.Add(Path.Combine(home, ".grok", "auth.json"));
        candidates.Add(Path.Combine(home, ".grok-cli", "auth.json"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
