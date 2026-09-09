using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaDock.Models;

namespace QuotaDock.Providers;

/// <summary>
/// Local credential discovery: environment variables first, then known login files written by
/// the vendor's own CLI / desktop app. Read-only; nothing is ever written back.
/// </summary>
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
            var toml = Regex.Match(trimmed, @"(?:api_key|access_token|token)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
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

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[]
                 {
                     "accessToken", "access_token", "api_key", "apiKey", "token", "key",
                     "authToken", "CODEBUDDY_AUTH_TOKEN", "jobToken", "pat", "refreshToken"
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

        foreach (var name in new[] { "auth", "credentials", "oauth", "data" })
        {
            if (element.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                var value = ExtractElement(nested);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}

internal enum DomesticAuthStyle
{
    Bearer,
    Bare
}

internal sealed record DomesticEndpoint(
    string Url,
    bool Post = false,
    string? Body = null,
    DomesticAuthStyle Auth = DomesticAuthStyle.Bearer,
    IReadOnlyDictionary<string, string>? Headers = null);

internal sealed record DomesticParseResult(bool AuthFailed, IReadOnlyList<QuotaWindow> Windows, string? Message)
{
    public static DomesticParseResult Empty(string? message = null) => new(false, [], message);

    public static DomesticParseResult Auth(string? message = null) => new(true, [], message);

    public static DomesticParseResult Ok(IReadOnlyList<QuotaWindow> windows) => new(false, windows, null);
}

internal sealed record DomesticSpec(
    string Id,
    string DisplayName,
    string Glyph,
    string AccentHex,
    string LoginHint,
    string[] EnvKeys,
    string[] FileHints,
    DomesticEndpoint[] Endpoints,
    Func<JsonElement, DomesticParseResult> Parse);

internal sealed record DomesticHttpResult(bool AuthFailed, bool SawSuccess, JsonDocument? Document, bool NetworkError);

internal static class DomesticHttp
{
    public static async Task<DomesticHttpResult> SendAsync(
        HttpClient httpClient,
        DomesticEndpoint endpoint,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(endpoint.Post ? HttpMethod.Post : HttpMethod.Get, endpoint.Url);
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                endpoint.Auth == DomesticAuthStyle.Bare ? token : $"Bearer {token}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("User-Agent", "AiDocks/0.3");
            if (endpoint.Headers is { } headers)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (endpoint.Post)
            {
                request.Content = new StringContent(endpoint.Body ?? "{}", Encoding.UTF8, "application/json");
            }

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new DomesticHttpResult(true, false, null, false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DomesticHttpResult(false, false, null, false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new DomesticHttpResult(false, true, document, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DomesticHttpResult(false, false, null, true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            return new DomesticHttpResult(false, false, null, true);
        }
    }
}

/// <summary>
/// One provider per brand. The domestic and international products of a brand share the same
/// API path and payload shape and differ only by host (verified: MiniMax answers both hosts with
/// the identical <c>base_resp</c> envelope; GLM's quota path is <c>/api/monitor/usage/quota/limit</c>
/// on both <c>open.bigmodel.cn</c> and <c>api.z.ai</c>). So the region is resolved by probing hosts
/// in order instead of by asking the user to pick a region.
/// </summary>
internal sealed class DomesticQuotaProvider(HttpClient httpClient, DomesticSpec spec) : IQuotaProvider
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
            return ProviderResult.Unavailable(this, $"{spec.DisplayName} 登录文件无法读取");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ProviderResult.Missing(this, spec.LoginHint);
        }

        var sawSuccessStatus = false;
        var authFailed = false;
        var networkFailed = false;

        foreach (var endpoint in spec.Endpoints)
        {
            var result = await DomesticHttp.SendAsync(httpClient, endpoint, token, cancellationToken).ConfigureAwait(false);
            if (result.AuthFailed)
            {
                authFailed = true;
                continue;
            }

            if (result.NetworkError)
            {
                networkFailed = true;
                continue;
            }

            if (result.Document is not { } document)
            {
                continue;
            }

            using (document)
            {
                sawSuccessStatus = true;
                var parsed = spec.Parse(document.RootElement);
                if (parsed.AuthFailed)
                {
                    authFailed = true;
                    continue;
                }

                if (parsed.Windows.Count > 0)
                {
                    return new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, parsed.Windows, DateTimeOffset.Now, ProviderState.Ready);
                }
            }
        }

        if (authFailed)
        {
            return ProviderResult.AuthenticationRequired(this, $"请重新登录 {spec.DisplayName}");
        }

        if (sawSuccessStatus)
        {
            return ProviderResult.Unavailable(this, $"{spec.DisplayName} 未识别到额度字段");
        }

        return networkFailed
            ? ProviderResult.Unavailable(this, $"{spec.DisplayName} 网络不可用")
            : ProviderResult.Unavailable(this, $"{spec.DisplayName} 接口未响应");
    }
}

/// <summary>
/// WorkBuddy / CodeBuddy needs two calls: the gas-station summary carries the precise, already
/// aggregated numbers, while the per-resource endpoint carries the package names and the cycle
/// end dates the summary omits. Both are keyed by PackageCode, so the payloads are merged.
/// </summary>
internal sealed class WorkBuddyQuotaProvider(
    HttpClient httpClient,
    DomesticEndpoint[]? summaryEndpoints = null,
    DomesticEndpoint? resourceEndpoint = null) : IQuotaProvider
{
    private static readonly DomesticEndpoint[] DefaultSummaryEndpoints =
    [
        new("https://www.codebuddy.cn/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}"""),
        new("https://www.workbuddy.cn/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}"""),
        new("https://www.workbuddy.ai/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}"""),
        new("https://www.codebuddy.ai/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}""")
    ];

    private static readonly DomesticEndpoint DefaultResourceEndpoint =
        new("https://copilot.tencent.com/v2/billing/meter/get-user-resource", Post: true, Body: """{"productCode":"p_tcaca"}""");

    private readonly DomesticEndpoint[] _summaryEndpoints = summaryEndpoints ?? DefaultSummaryEndpoints;
    private readonly DomesticEndpoint _resourceEndpoint = resourceEndpoint ?? DefaultResourceEndpoint;

    public string Id => "workbuddy";
    public string DisplayName => "WorkBuddy";
    public string Glyph => "▣";
    public string AccentHex => "#2A9D8F";

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? token;
        try
        {
            token = LocalCredential.ReadToken(
                ["CODEBUDDY_AUTH_TOKEN", "WORKBUDDY_TOKEN"],
                [
                    @"{LocalAppData}\CodeBuddyExtension\Data\Public\auth\workbuddy-desktop.info",
                    @"{LocalAppData}\CodeBuddyExtension\Data\Public\auth\codebuddy-desktop.info",
                    @"{Home}\.codebuddy\settings.json",
                    @"{Home}\.workbuddy\auth.json"
                ]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderResult.Unavailable(this, "WorkBuddy 登录文件无法读取");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return ProviderResult.Missing(this, "请先登录 WorkBuddy / CodeBuddy 桌面端，或设置 CODEBUDDY_AUTH_TOKEN。");
        }

        var authFailed = false;
        var networkFailed = false;
        JsonDocument? summary = null;
        JsonDocument? resource = null;

        try
        {
            foreach (var endpoint in _summaryEndpoints)
            {
                var result = await DomesticHttp.SendAsync(httpClient, endpoint, token, cancellationToken).ConfigureAwait(false);
                if (result.AuthFailed)
                {
                    authFailed = true;
                    continue;
                }

                if (result.NetworkError)
                {
                    networkFailed = true;
                    continue;
                }

                if (result.Document is { } document)
                {
                    summary = document;
                    break;
                }
            }

            var resourceResult = await DomesticHttp.SendAsync(httpClient, _resourceEndpoint, token, cancellationToken).ConfigureAwait(false);
            if (resourceResult.AuthFailed)
            {
                authFailed = true;
            }
            else if (resourceResult.NetworkError)
            {
                networkFailed = true;
            }
            else
            {
                resource = resourceResult.Document;
            }

            if (summary is not null || resource is not null)
            {
                var parsed = DomesticProviders.WorkBuddy(summary?.RootElement, resource?.RootElement);
                if (parsed.AuthFailed)
                {
                    authFailed = true;
                }
                else if (parsed.Windows.Count > 0)
                {
                    return new QuotaSnapshot(Id, DisplayName, Glyph, AccentHex, parsed.Windows, DateTimeOffset.Now, ProviderState.Ready);
                }
            }
        }
        finally
        {
            summary?.Dispose();
            resource?.Dispose();
        }

        if (authFailed)
        {
            return ProviderResult.AuthenticationRequired(this, "请重新登录 WorkBuddy");
        }

        return networkFailed
            ? ProviderResult.Unavailable(this, "WorkBuddy 网络不可用")
            : ProviderResult.Unavailable(this, "WorkBuddy 未识别到额度字段");
    }
}

/// <summary>
/// Endpoint, credential and payload definitions for the six domestic subscription AI products.
/// Every parser is written against the payload the vendor's own client sends/receives; see
/// ATTRIBUTION.md for the references.
/// </summary>
internal static class DomesticProviders
{
    public static readonly DomesticSpec[] All =
    [
        new(
            "kimi", "Kimi", "◐", "#F5C518",
            "请先运行 kimi login（Kimi Code CLI），或设置 KIMI_API_KEY。",
            ["KIMI_API_KEY", "KIMI_CODE_API_KEY", "MOONSHOT_API_KEY"],
            [@"{Home}\.kimi-code\credentials.json", @"{Home}\.kimi-code\config.toml", @"{Home}\.kimi\config.toml"],
            [new DomesticEndpoint("https://api.kimi.com/coding/v1/usages")],
            Kimi),

        new(
            "workbuddy", "WorkBuddy", "▣", "#2A9D8F",
            "请先登录 WorkBuddy / CodeBuddy 桌面端，或设置 CODEBUDDY_AUTH_TOKEN。",
            ["CODEBUDDY_AUTH_TOKEN", "WORKBUDDY_TOKEN"],
            [
                @"{LocalAppData}\CodeBuddyExtension\Data\Public\auth\workbuddy-desktop.info",
                @"{LocalAppData}\CodeBuddyExtension\Data\Public\auth\codebuddy-desktop.info",
                @"{Home}\.codebuddy\settings.json",
                @"{Home}\.workbuddy\auth.json"
            ],
            [
                new DomesticEndpoint("https://www.codebuddy.cn/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}"""),
                new DomesticEndpoint("https://www.workbuddy.cn/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}"""),
                new DomesticEndpoint("https://www.workbuddy.ai/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}"""),
                new DomesticEndpoint("https://www.codebuddy.ai/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}""")
            ],
            root => WorkBuddy(root)),

        new(
            "glm", "GLM", "▲", "#0F6FFF",
            "请先订阅 GLM Coding Plan 并设置智谱 API Key（BIGMODEL_API_KEY / ZAI_API_KEY）。",
            ["BIGMODEL_API_KEY", "ZHIPU_API_KEY", "GLM_API_KEY", "ZAI_API_KEY", "ZAI_KEY"],
            [@"{Home}\.zcode\credentials.json", @"{Home}\.bigmodel\api_key", @"{Home}\.zai\api_key"],
            [
                new DomesticEndpoint("https://open.bigmodel.cn/api/monitor/usage/quota/limit", Auth: DomesticAuthStyle.Bare),
                new DomesticEndpoint("https://api.z.ai/api/monitor/usage/quota/limit")
            ],
            Glm),

        new(
            "qoder", "Qoder", "◆", "#FF6A00",
            "请先登录 Qoder 桌面端，或设置 QODER_PAT（pt- 开头）。",
            ["QODER_PAT", "QODER_TOKEN", "QODER_API_KEY"],
            [
                @"{Home}\.qoder-cn\credentials.json",
                @"{Home}\.qoder\credentials.json",
                @"{AppData}\Qoder CN\User\globalStorage\credentials.json"
            ],
            [
                new DomesticEndpoint("https://openapi.qoder.com.cn/api/v2/quota/usage"),
                new DomesticEndpoint("https://openapi.qoder.sh/api/v2/quota/usage")
            ],
            Qoder),

        new(
            "trae", "Trae", "▸", "#3B82F6",
            "请先登录 Trae 桌面端，或设置 TRAE_TOKEN / TRAE_CN_TOKEN。",
            ["TRAE_TOKEN", "TRAE_CN_TOKEN", "TRAE_INTL_TOKEN"],
            [
                @"{AppData}\Trae CN\User\globalStorage\storage.json",
                @"{AppData}\Trae\User\globalStorage\storage.json",
                @"{AppData}\TRAE SOLO CN\User\globalStorage\storage.json"
            ],
            [
                new DomesticEndpoint("https://api.trae.cn/trae/api/v2/pay/ide_user_ent_usage", Post: true, Body: "{}"),
                new DomesticEndpoint("https://api.trae.ai/trae/api/v2/pay/ide_user_ent_usage", Post: true, Body: "{}")
            ],
            Trae),

        new(
            "minimax", "MiniMax", "▬", "#E11D48",
            "请先订阅 MiniMax Token Plan 并设置 MINIMAX_API_KEY。",
            ["MINIMAX_API_KEY", "MINIMAX_CN_API_KEY", "MINIMAX_INTL_API_KEY"],
            [@"{Home}\.mmx\config.json", @"{Home}\.minimax\credentials.json"],
            [
                new DomesticEndpoint("https://www.minimaxi.com/v1/token_plan/remains"),
                new DomesticEndpoint("https://www.minimax.io/v1/token_plan/remains")
            ],
            MiniMax)
    ];

    public static DomesticQuotaProvider? Create(HttpClient httpClient, string id)
    {
        var spec = All.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return spec is null ? null : new DomesticQuotaProvider(httpClient, spec);
    }

    /// <summary>Parser entry point used by the self-test fixtures.</summary>
    internal static DomesticParseResult Parse(string id, string json)
    {
        var spec = All.First(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        using var document = JsonDocument.Parse(json);
        return spec.Parse(document.RootElement);
    }

    /// <summary>Parser delegate for a brand, used by the self-test to build a loopback spec.</summary>
    internal static Func<JsonElement, DomesticParseResult> ParserFor(string id) =>
        All.First(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Parse;

    // ---------------------------------------------------------------- parsers

    /// <summary>
    /// Kimi Code subscription quota. Official CLI source: MoonshotAI/kimi-code
    /// <c>packages/oauth/src/managed-usage.ts</c> — <c>GET /coding/v1/usages</c> with an OAuth Bearer
    /// token, returning <c>usage{limit,used,remaining,resetTime}</c> (weekly),
    /// <c>limits[]{window{duration,timeUnit}, detail{limit,remaining,resetTime}}</c> (rolling 5h) and
    /// <c>totalQuota{limit,remaining}</c> (total subscription credits, no reset).
    /// </summary>
    private static DomesticParseResult Kimi(JsonElement root)
    {
        var data = Unwrap(root, "usage", "limits", "totalQuota");
        var windows = new List<QuotaWindow>();
        var weekly = false;

        foreach (var item in Array(data, "limits"))
        {
            var detail = Prop(item, "detail") ?? item;
            var window = Prop(item, "window");
            var duration = KimiWindow(window) ?? KimiWindow(item);
            var label = duration is null ? "5 小时限额" : ProviderParsing.WindowLabel(duration, "5 小时限额");
            if (label == "周限额")
            {
                weekly = true;
            }

            AddLimitRow(windows, label, detail, duration);
        }

        if (!weekly && Prop(data, "usage") is { ValueKind: JsonValueKind.Object } usage)
        {
            AddLimitRow(windows, "周限额", usage, TimeSpan.FromDays(7));
        }

        if (Prop(data, "totalQuota") is { ValueKind: JsonValueKind.Object } total)
        {
            AddLimitRow(windows, "总订阅额度", total, null);
        }

        if (windows.Count == 0)
        {
            var message = Text(data, "message", "msg");
            return message is not null && message.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? DomesticParseResult.Auth(message)
                : DomesticParseResult.Empty();
        }

        return DomesticParseResult.Ok(Order(windows));
    }

    /// <summary>
    /// WorkBuddy / CodeBuddy credits. Live-verified on the CN gas station with the desktop app's
    /// access token. The summary endpoint carries the precise, already-aggregated numbers
    /// (<c>data.Packages[].CycleTotalCapacity/CycleRemainCapacity/CycleUsedCapacity</c>, sent as
    /// strings); the per-resource endpoint carries the package names and the cycle end dates the
    /// summary omits (<c>data.Response.Data.Accounts[].PackageName/SubProductName/CycleEndTime</c>),
    /// but rounds consumption to whole credits. Both payloads are keyed by PackageCode, so they are
    /// merged per package: numbers from the summary, dates and names from the resource list.
    /// </summary>
    internal static DomesticParseResult WorkBuddy(JsonElement? summaryRoot, JsonElement? resourceRoot = null)
    {
        var packages = new List<WorkBuddyRow>();

        if (summaryRoot is { } summary)
        {
            if (Num(summary, "code") is 401 or 403)
            {
                return DomesticParseResult.Auth();
            }

            var data = Unwrap(summary, "Packages");
            foreach (var row in Array(data, "Packages"))
            {
                var total = Num(row, "CycleTotalCapacity", "TotalCapacity", "CapacitySize");
                if (total is not > 0)
                {
                    continue;
                }

                var used = Num(row, "CycleUsedCapacity", "CapacityUsed");
                var remain = Num(row, "CycleRemainCapacity", "CapacityRemain");
                if (used is null && remain is not null)
                {
                    used = total.Value - remain.Value;
                }

                packages.Add(new WorkBuddyRow(
                    Text(row, "PackageCode") ?? "",
                    total.Value,
                    Math.Max(0, used ?? 0),
                    null,
                    null,
                    null));
            }
        }

        if (resourceRoot is { } resource)
        {
            if (Num(resource, "code") is 401 or 403)
            {
                return DomesticParseResult.Auth();
            }

            foreach (var account in Objects(resource))
            {
                var code = Text(account, "PackageCode");
                var total = Num(account, "CycleCapacitySize", "CapacitySize");
                if (code is null || total is not > 0)
                {
                    continue;
                }

                var name = Text(account, "PackageName");
                var sub = Text(account, "SubProductName");
                var reset = Reset(account, "CycleEndTime", "ExpiredTime");
                var index = packages.FindIndex(item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var existing = packages[index];
                    packages[index] = existing with
                    {
                        Name = existing.Name ?? name,
                        Sub = existing.Sub ?? sub,
                        ResetsAt = existing.ResetsAt is null
                            ? reset
                            : reset is null ? existing.ResetsAt : (reset.Value < existing.ResetsAt.Value ? reset : existing.ResetsAt)
                    };
                }
                else
                {
                    packages.Add(new WorkBuddyRow(
                        code,
                        total.Value,
                        Math.Max(0, Num(account, "CycleCapacityUsed", "CapacityUsed") ?? 0),
                        name,
                        sub,
                        reset));
                }
            }
        }

        if (packages.Count == 0)
        {
            var message = Text(summaryRoot, "msg", "message") ?? Text(resourceRoot, "msg", "message");
            return message is not null && message.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? DomesticParseResult.Auth(message)
                : DomesticParseResult.Empty(message);
        }

        var unique = packages
            .GroupBy(package => package.Code)
            .Select(group => group.First())
            .OrderByDescending(package => package.Total)
            .Take(3)
            .ToList();
        // The resource payload supplies the names that tell the plan apart from the granted
        // packages. When it is unavailable (international hosts), fall back to size order.
        var named = unique.Any(package => package.Name is not null || package.Sub is not null);
        var windows = unique
            .Select((package, index) => new QuotaWindow(
                named
                    ? WorkBuddyLabel(package)
                    : index == 0 ? "订阅额度" : "附加额度",
                Percent(package.Used, package.Total),
                package.ResetsAt,
                null))
            .ToList();
        if (named)
        {
            windows = windows.OrderByDescending(window => window.Label == "订阅额度").ToList();
        }

        return DomesticParseResult.Ok(Order(windows));
    }

    private sealed record WorkBuddyRow(
        string Code,
        double Total,
        double Used,
        string? Name,
        string? Sub,
        DateTimeOffset? ResetsAt);

    /// <summary>The gas station calls the plan "套餐基础积分" and referral/trial grants "平台奖励积分";
    /// the sub-product name marks the latter as a 赠送包.</summary>
    private static string WorkBuddyLabel(WorkBuddyRow package)
    {
        var text = $"{package.Name} {package.Sub}";
        return text.Contains("赠送", StringComparison.Ordinal) ||
               text.Contains("奖励", StringComparison.Ordinal) ||
               text.Contains("裂变", StringComparison.Ordinal) ||
               text.Contains("bonus", StringComparison.OrdinalIgnoreCase)
            ? "奖励额度"
            : "订阅额度";
    }

    /// <summary>
    /// GLM Coding Plan. <c>GET /api/monitor/usage/quota/limit</c> on <c>open.bigmodel.cn</c> (key sent
    /// bare in Authorization) or <c>api.z.ai</c> (Bearer). Payload: <c>data.limits[]</c> rows of
    /// <c>type</c> TOKENS_LIMIT/CREDIT_LIMIT (TIME_LIMIT is the MCP search allowance and is ignored),
    /// <c>percentage</c> (consumed share), <c>unit</c> 3=hours / 6=weeks with <c>number</c>, and
    /// <c>nextResetTime</c> in unix milliseconds.
    /// </summary>
    private static DomesticParseResult Glm(JsonElement root)
    {
        var data = Unwrap(root, "limits");
        var windows = new List<QuotaWindow>();

        foreach (var row in Array(data, "limits"))
        {
            var type = Text(row, "type");
            if (type is not null &&
                !type.Equals("TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("CREDIT_LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var percent = NormalizePercent(Num(row, "percentage"));
            if (percent is null)
            {
                var used = Num(row, "currentValue", "usage", "used");
                var limit = Num(row, "limit", "total");
                if (used is not null && limit is > 0)
                {
                    percent = Percent(used.Value, limit.Value);
                }
            }

            if (percent is null)
            {
                continue;
            }

            var unit = Num(row, "unit");
            var number = Num(row, "number") ?? 1;
            TimeSpan? duration = unit switch
            {
                3 => TimeSpan.FromHours(number),
                6 => TimeSpan.FromDays(7 * number),
                _ => null
            };
            var label = duration is null ? "订阅周期" : ProviderParsing.WindowLabel(duration, "订阅周期");
            windows.Add(new QuotaWindow(label, percent.Value, Reset(row, "nextResetTime", "resetTime"), duration));
        }

        if (windows.Count == 0)
        {
            // Legacy payload: percentages directly on data (or data.quota).
            var legacy = Prop(data, "quota") ?? data;
            AddPercentRow(windows, "5 小时限额", legacy, TimeSpan.FromHours(5), "fiveHourPercent", "fiveHourUsage");
            AddPercentRow(windows, "周限额", legacy, TimeSpan.FromDays(7), "weeklyPercent", "weeklyUsage");
            AddPercentRow(windows, "订阅周期", legacy, null, "monthlyPercent");
        }

        if (windows.Count == 0)
        {
            var code = Num(data, "code") ?? Num(root, "code");
            var message = Text(data, "msg", "message") ?? Text(root, "msg", "message");
            return code is 1001 or 401 || (message?.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ?? false)
                ? DomesticParseResult.Auth(message)
                : DomesticParseResult.Empty(message);
        }

        return DomesticParseResult.Ok(Order(windows));
    }

    /// <summary>
    /// Qoder credits. <c>GET {openapi}/api/v2/quota/usage</c> with the account token as a plain
    /// Bearer (CN host <c>openapi.qoder.com.cn</c>, international <c>openapi.qoder.sh</c>). Payload:
    /// <c>userQuota{total,used,remaining}</c> (plan credits) plus <c>addOnQuota{...}</c>
    /// (bonus / check-in credits), a precomputed <c>totalUsagePercentage</c>, and <c>expiresAt</c>
    /// (plan expiry, ms epoch) — Qoder has no rolling window, so the plan expiry is the only date.
    /// </summary>
    private static DomesticParseResult Qoder(JsonElement root)
    {
        var data = Unwrap(root, "userQuota", "addOnQuota", "totalUsagePercentage");
        var windows = new List<QuotaWindow>();
        var expiresAt = Reset(data, "expiresAt", "expireTime", "expires_at", "expiredTime");

        AddQuotaRow(windows, "基础额度", Prop(data, "userQuota"), expiresAt);
        AddQuotaRow(windows, "赠送额度", Prop(data, "addOnQuota"), expiresAt);

        if (windows.Count == 0 && NormalizePercent(Num(data, "totalUsagePercentage")) is { } overall)
        {
            windows.Add(new QuotaWindow("订阅周期", overall, expiresAt, null));
        }

        if (windows.Count == 0)
        {
            var message = Text(data, "message", "msg", "errorMessage");
            return message is not null && message.Contains("token", StringComparison.OrdinalIgnoreCase)
                ? DomesticParseResult.Auth(message)
                : DomesticParseResult.Empty(message);
        }

        return DomesticParseResult.Ok(Order(windows));
    }

    /// <summary>
    /// Trae subscription packs. <c>POST {api.trae.cn|api.trae.ai}/trae/api/v2/pay/ide_user_ent_usage</c>
    /// returns <c>user_entitlement_pack_list[]</c>; each pack keeps its quota in
    /// <c>entitlement_base_info.quota</c> or (older shape) <c>product_extra.subscription_extra.quota</c> /
    /// <c>product_extra.package_extra.quota</c> with <c>basic_usage_limit</c> + <c>bonus_usage_limit</c>
    /// and consumption in <c>usage.basic_usage_amount</c> / <c>bonus_usage_amount</c>. Packs with
    /// <c>product_type == 3</c> (promo) or <c>is_hide</c>/<c>status == 3</c> (cancelled) are skipped.
    /// </summary>
    private static DomesticParseResult Trae(JsonElement root)
    {
        var data = Unwrap(root, "user_entitlement_pack_list");
        var packs = Array(data, "user_entitlement_pack_list");
        var windows = new List<QuotaWindow>();

        foreach (var pack in packs)
        {
            var baseInfo = Prop(pack, "entitlement_base_info") ?? pack;
            if (Num(baseInfo, "product_type") is 3)
            {
                continue;
            }

            if (Bool(baseInfo, "is_hide") == true || Num(baseInfo, "status") is 3)
            {
                continue;
            }

            var quota = FirstQuota(baseInfo);
            if (quota is null)
            {
                continue;
            }

            var limit = Num(quota.Value, "basic_usage_limit", "credits_limit");
            var used = Num(Prop(pack, "usage"), "basic_usage_amount", "credits_amount") ?? 0;
            var bonusLimit = Num(quota.Value, "bonus_usage_limit");
            if (bonusLimit is > 0)
            {
                var bonusUsed = Num(Prop(pack, "usage"), "bonus_usage_amount") ?? 0;
                limit += Math.Max(0, bonusLimit.Value - bonusUsed);
            }

            if (limit is null or <= 0)
            {
                continue;
            }

            var label = Text(pack, "display_desc") ?? "订阅额度";
            windows.Add(new QuotaWindow(label, Percent(used, limit.Value), Reset(baseInfo, "end_time"), null));
        }

        if (windows.Count == 0)
        {
            var message = Text(data, "message", "msg", "error");
            return message is not null && message.Contains("token", StringComparison.OrdinalIgnoreCase)
                ? DomesticParseResult.Auth(message)
                : DomesticParseResult.Empty(message);
        }

        return DomesticParseResult.Ok(Order(windows));
    }

    /// <summary>
    /// MiniMax Token Plan. <c>GET {host}/v1/token_plan/remains</c> with the API key as a Bearer
    /// (<c>www.minimaxi.com</c> domestic, <c>www.minimax.io</c> international — both hosts answer
    /// with the same <c>base_resp</c> envelope). The payload lists every model under
    /// <c>model_remains[]</c>, each with an interval (5h) and a weekly window:
    /// <c>current_*_remaining_percent</c> (the primary signal; on Token Plans the counts are 0),
    /// <c>current_*_total_count</c> / <c>current_*_usage_count</c> (where <c>*_usage_count</c> holds
    /// the <b>remaining</b> amount — MiniMax-AI/MiniMax-M2#99), <c>end_time</c> and
    /// <c>weekly_end_time</c> (ms epoch reset), and <c>current_weekly_status</c> (0 = the plan has no
    /// weekly window).
    /// </summary>
    private static DomesticParseResult MiniMax(JsonElement root)
    {
        var models = Array(Unwrap(root, "model_remains"), "model_remains").ToList();
        if (models.Count == 0)
        {
            var baseResp = Prop(root, "base_resp");
            var status = Num(baseResp, "status_code");
            var message = Text(baseResp, "status_msg") ?? Text(root, "message", "msg");
            return status is 1004 or 1002 or 1003
                ? DomesticParseResult.Auth(message)
                : DomesticParseResult.Empty(message);
        }

        // The list holds every model (general, video, ...); the chat model carries the plan window.
        var model = FirstOrNull(models, item => (Text(item, "model_name") ?? "").StartsWith("minimax-m", StringComparison.OrdinalIgnoreCase))
                    ?? FirstOrNull(models, item => Num(item, "current_interval_remaining_percent") is not null)
                    ?? models[0];

        var windows = new List<QuotaWindow>();
        if (MiniMaxWindow(model, "interval", "5 小时限额", TimeSpan.FromHours(5)) is { } interval)
        {
            windows.Add(interval);
        }

        if (Num(model, "current_weekly_status") is not 0 &&
            MiniMaxWindow(model, "weekly", "周限额", TimeSpan.FromDays(7)) is { } weekly)
        {
            windows.Add(weekly);
        }

        if (windows.Count == 0)
        {
            return DomesticParseResult.Empty(Text(Prop(root, "base_resp"), "status_msg"));
        }

        return DomesticParseResult.Ok(Order(windows));
    }

    private static QuotaWindow? MiniMaxWindow(JsonElement model, string prefix, string label, TimeSpan duration)
    {
        var remainingPercent = Num(model, $"current_{prefix}_remaining_percent");
        double? percent = remainingPercent is null
            ? null
            : Math.Clamp(100 - remainingPercent.Value, 0, 100);
        if (percent is null)
        {
            var total = Num(model, $"current_{prefix}_total_count");
            var remaining = Num(model, $"current_{prefix}_usage_count", $"current_{prefix}_remaining_count");
            if (total is > 0 && remaining is not null)
            {
                percent = Percent(Math.Max(0, total.Value - remaining.Value), total.Value);
            }
        }

        if (percent is null)
        {
            return null;
        }

        var reset = prefix == "interval"
            ? Reset(model, "end_time", "next_reset_time", "reset_time")
            : Reset(model, "weekly_end_time", "next_weekly_reset_time");
        return new QuotaWindow(label, percent.Value, reset, duration);
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<QuotaWindow> Order(List<QuotaWindow> windows) =>
        windows
            .GroupBy(window => window.Label)
            .Select(group => group.First())
            .OrderBy(window => window.Duration?.TotalHours ?? double.MaxValue)
            .Take(3)
            .ToArray();

    private static JsonElement Unwrap(JsonElement root, params string[] keys)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out _))
            {
                return root;
            }
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            return data;
        }

        return root;
    }

    private static JsonElement? Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var node) && node.ValueKind != JsonValueKind.Null
            ? node
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var node) &&
            node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<JsonElement> Objects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in Objects(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in Objects(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static double? Num(JsonElement? element, params string[] names)
    {
        if (element is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var number))
            {
                return number;
            }

            if (node.ValueKind == JsonValueKind.String &&
                double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? Bool(JsonElement? element, string name)
    {
        if (element is not { } value || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var node))
        {
            return null;
        }

        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? Text(JsonElement? element, params string[] names)
    {
        if (element is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String)
            {
                var text = node.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? Reset(JsonElement? element, params string[] names)
    {
        if (element is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var node))
            {
                continue;
            }

            if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var raw))
            {
                if (raw > 1_000_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(raw);
                }

                if (raw > 1_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(raw);
                }
            }

            if (node.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(node.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double Percent(double used, double total) =>
        total <= 0 ? 0 : Math.Clamp(used / total * 100, 0, 100);

    private static JsonElement? FirstOrNull(List<JsonElement> items, Func<JsonElement, bool> predicate)
    {
        foreach (var item in items)
        {
            if (predicate(item))
            {
                return item;
            }
        }

        return null;
    }

    private static double? NormalizePercent(double? value) =>
        value is null ? null : Math.Clamp(value.Value is > 1 and <= 100 ? value.Value : value.Value <= 1 ? value.Value * 100 : value.Value, 0, 100);

    private static void AddLimitRow(List<QuotaWindow> windows, string label, JsonElement row, TimeSpan? duration)
    {
        var limit = Num(row, "limit", "total");
        var used = Num(row, "used");
        var remaining = Num(row, "remaining", "remain");
        double? percent = null;
        if (limit is > 0 && used is not null)
        {
            percent = Percent(used.Value, limit.Value);
        }
        else if (limit is > 0 && remaining is not null)
        {
            percent = Percent(Math.Max(0, limit.Value - remaining.Value), limit.Value);
        }
        else
        {
            // Some payloads expose utilisation directly when limit/used arithmetic is absent.
            percent = NormalizePercent(Num(row, "utilization", "percent", "usedPercent", "used_percent", "percentage"));
        }

        if (percent is null)
        {
            return;
        }

        windows.Add(new QuotaWindow(
            label,
            percent.Value,
            Reset(row, "resetTime", "resetAt", "reset_time", "reset_at", "resetsAt", "nextResetTime"),
            duration));
    }

    private static void AddPercentRow(List<QuotaWindow> windows, string label, JsonElement element, TimeSpan? duration, params string[] names)
    {
        if (NormalizePercent(Num(element, names)) is { } percent)
        {
            windows.Add(new QuotaWindow(label, percent, null, duration));
        }
    }

    private static void AddQuotaRow(List<QuotaWindow> windows, string label, JsonElement? element, DateTimeOffset? resetsAt = null)
    {
        var total = Num(element, "total", "totalCount");
        var used = Num(element, "used", "usedCount");
        var remaining = Num(element, "remaining", "remain", "remainingCount");
        double? percent = null;
        if (total is > 0 && used is not null)
        {
            percent = Percent(used.Value, total.Value);
        }
        else if (total is > 0 && remaining is not null)
        {
            percent = Percent(Math.Max(0, total.Value - remaining.Value), total.Value);
        }

        if (percent is not null)
        {
            windows.Add(new QuotaWindow(label, percent.Value, resetsAt, null));
        }
    }

    private static JsonElement? FirstQuota(JsonElement baseInfo)
    {
        if (Prop(baseInfo, "quota") is { } direct && Num(direct, "basic_usage_limit", "bonus_usage_limit", "credits_limit", "premium_model_fast_request_limit") is not null)
        {
            return direct;
        }

        if (Prop(baseInfo, "product_extra") is { } extra)
        {
            if (Prop(extra, "subscription_extra") is { } subscription && Prop(subscription, "quota") is { } subscriptionQuota)
            {
                return subscriptionQuota;
            }

            if (Prop(extra, "package_extra") is { } package && Prop(package, "quota") is { } packageQuota)
            {
                return packageQuota;
            }
        }

        return null;
    }

    private static TimeSpan? KimiWindow(JsonElement? window)
    {
        var duration = Num(window, "duration", "value");
        var unit = Text(window, "timeUnit", "unit");
        if (duration is null)
        {
            return null;
        }

        return unit?.ToUpperInvariant() switch
        {
            "MINUTE" or "MINUTES" or "MIN" => TimeSpan.FromMinutes(duration.Value),
            "HOUR" or "HOURS" or "H" => TimeSpan.FromHours(duration.Value),
            "DAY" or "DAYS" or "D" => TimeSpan.FromDays(duration.Value),
            "WEEK" or "WEEKS" or "W" => TimeSpan.FromDays(7 * duration.Value),
            _ => duration.Value <= 24 ? TimeSpan.FromHours(duration.Value) : TimeSpan.FromDays(duration.Value)
        };
    }
}

