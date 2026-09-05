using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using QuotaDock.Models;
using QuotaDock.Native;

namespace QuotaDock.Providers;

public sealed class CursorQuotaProvider(HttpClient httpClient) : IQuotaProvider
{
    private const string UsageUrl = "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage";

    public string Id => "cursor";
    public string DisplayName => "Cursor Usage";
    public string Glyph => "▸";
    public string AccentHex => "#F4F4F5";

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? token;
        try
        {
            token = ReadAccessToken();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            return ProviderResult.Unavailable(this, "Cursor 登录文件无法读取");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ProviderResult.Missing(this, "请先登录 Cursor 桌面应用");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, UsageUrl)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        request.Headers.UserAgent.ParseAdd("QuotaDock/0.5.1");

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ProviderResult.AuthenticationRequired(this, "请重新登录 Cursor");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult.Unavailable(this, $"Cursor 返回 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var windows = await ParseUsageAsync(stream, cancellationToken);
            return windows.Count == 0
                ? ProviderResult.Unavailable(this, "Cursor 未返回可用额度窗口")
                : new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, windows, DateTimeOffset.Now, ProviderState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProviderResult.Unavailable(this, "Cursor 请求超时");
        }
        catch (HttpRequestException)
        {
            return ProviderResult.Unavailable(this, "Cursor 网络不可用");
        }
        catch (Exception)
        {
            return ProviderResult.Unavailable(this, "Cursor 暂时无法读取");
        }
    }

    internal static async Task<IReadOnlyList<QuotaWindow>> ParseUsageAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("planUsage", out var plan) || plan.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            DateTimeOffset? resetAt = ReadUnixMilliseconds(root, "billingCycleEnd");
            DateTimeOffset? start = ReadUnixMilliseconds(root, "billingCycleStart");
            TimeSpan? duration = start is not null && resetAt is not null ? resetAt.Value - start.Value : TimeSpan.FromDays(30);

            var result = new List<QuotaWindow>(2);
            AddWindow(result, plan, "autoPercentUsed", "包含用量", resetAt, duration);
            AddWindow(result, plan, "apiPercentUsed", "API 用量", resetAt, duration);
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string? ReadAccessTokenFromDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            return QueryAccessToken(databasePath);
        }
        catch (Exception)
        {
            var snapshot = CopySnapshot(databasePath);
            return snapshot is null ? null : QueryAccessToken(snapshot);
        }
    }

    private static string? ReadAccessToken()
    {
        foreach (var path in CredentialPaths())
        {
            var token = ReadAccessTokenFromDatabase(path);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static IEnumerable<string> CredentialPaths()
    {
        var configured = Environment.GetEnvironmentVariable("CURSOR_STATE_VSCDB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var product in new[] { "Cursor", "Cursor Nightly", "Cursor Dev" })
        {
            yield return Path.Combine(appData, product, "User", "globalStorage", "state.vscdb");
        }
    }

    private static string? QueryAccessToken(string databasePath)
    {
        const string sql = "SELECT value FROM ItemTable WHERE key = ? LIMIT 1";
        var value = WinSqlite.QueryText(databasePath, sql, "cursorAuth/accessToken")?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var legacy = WinSqlite.QueryText(databasePath, sql, "cursorAuthStatus")?.Trim();
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(legacy);
            return document.RootElement.TryGetProperty("accessToken", out var token)
                ? token.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? CopySnapshot(string databasePath)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "QuotaDock");
            Directory.CreateDirectory(directory);
            var snapshot = Path.Combine(directory, "cursor-state.vscdb");
            File.Copy(databasePath, snapshot, true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = databasePath + suffix;
                if (File.Exists(sidecar))
                {
                    File.Copy(sidecar, snapshot + suffix, true);
                }
            }

            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddWindow(
        ICollection<QuotaWindow> target,
        JsonElement plan,
        string property,
        string label,
        DateTimeOffset? resetAt,
        TimeSpan? duration)
    {
        if (!TryReadPercent(plan, property, out var percent))
        {
            return;
        }

        target.Add(new QuotaWindow(label, Math.Clamp(percent, 0, 100), resetAt, duration));
    }

    private static bool TryReadPercent(JsonElement plan, string property, out double percent)
    {
        percent = 0;
        if (!plan.TryGetProperty(property, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out percent),
            JsonValueKind.String => double.TryParse(element.GetString(), out percent),
            _ => false
        };
    }

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        long milliseconds = 0;
        var ok = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out milliseconds),
            JsonValueKind.String => long.TryParse(element.GetString(), out milliseconds),
            _ => false
        };
        if (!ok || milliseconds <= 0)
        {
            return null;
        }

        if (milliseconds < 10_000_000_000)
        {
            milliseconds *= 1000;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
