using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using TwinDock.Models;

namespace TwinDock.Services;

internal sealed class OfficialStatusClient : IDisposable
{
    private static readonly TimeSpan FreshTtl = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MissTtl = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly Regex RssItem = new("<item[\\s\\S]*?</item>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RssStatus = new(@"Status:\s*(RESOLVED|INVESTIGATING|IDENTIFIED|MONITORING|UPDATE|MAINTENANCE|CRITICAL|MAJOR)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] GeminiNeedles =
    [
        "gemini", "vertex ai", "vertexai", "generative ai", "generative language",
        "ai studio", "makersuite", "bard", "google ai"
    ];

    private readonly HttpClient _http;
    private readonly Dictionary<string, (DateTimeOffset At, OfficialStatus Value)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public OfficialStatusClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, application/rss+xml, text/xml, text/html;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    public async Task<IReadOnlyDictionary<string, OfficialStatus>> ReadAsync(
        IEnumerable<ProbeTarget> targets,
        bool demo,
        CancellationToken cancellationToken,
        HttpClient? via = null)
    {
        _ = via;
        var selected = targets.ToArray();
        if (demo)
        {
            return selected.ToDictionary(
                target => target.Id,
                target => Ok(target.Id),
                StringComparer.OrdinalIgnoreCase);
        }

        var hubTask = ReadHubAsync(cancellationToken);
        var tasks = selected.Select(target => ReadOneAsync(target, hubTask, cancellationToken)).ToArray();
        await Task.WhenAll(tasks);
        var map = new Dictionary<string, OfficialStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var value = await task;
            map[value.TargetId] = value;
        }

        return map;
    }

    public IReadOnlyDictionary<string, OfficialStatus> Peek(IEnumerable<ProbeTarget> targets)
    {
        lock (_gate)
        {
            return targets.ToDictionary(
                target => target.Id,
                target => _cache.TryGetValue(target.Id, out var cached) ? cached.Value : Unknown(target.Id),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static IReadOnlyList<string> UrlsFor(string id) => id.ToLowerInvariant() switch
    {
        "codex" or "chatgpt" =>
        [
            "https://modelstatus.ai/api/provider/openai/status",
            "https://r.jina.ai/https://status.openai.com/",
            "https://r.jina.ai/http://status.openai.com/",
            "https://status.openai.com/"
        ],
        "grok" =>
        [
            "https://status.x.ai/feed.xml",
            "https://r.jina.ai/https://status.x.ai/",
            "https://status.x.ai/"
        ],
        "claude" =>
        [
            "https://status.anthropic.com/api/v2/status.json",
            "https://status.anthropic.com/api/v2/summary.json",
            "https://modelstatus.ai/api/provider/anthropic/status",
            "https://r.jina.ai/https://status.claude.com/api/v2/status.json",
            "https://status.claude.com/api/v2/status.json"
        ],
        "gemini" =>
        [
            "https://status.cloud.google.com/incidents.json",
            "https://modelstatus.ai/api/provider/google/status",
            "https://r.jina.ai/https://aistudio.google.com/status"
        ],
        "cursor" =>
        [
            "https://status.cursor.com/api/v2/summary.json",
            "https://status.cursor.com/api/v2/status.json",
            "https://r.jina.ai/https://status.cursor.com/api/v2/status.json",
            "https://status.cursor.com/"
        ],
        _ => []
    };

    internal static OfficialStatus ParseBody(string targetId, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Unknown(targetId);
        }

        var text = Normalize(body);
        foreach (var json in ExtractJsonBlobs(text))
        {
            var parsed = ParseJson(targetId, json);
            if (parsed.Level is not OfficialLevel.Unknown)
            {
                return parsed;
            }
        }

        if (text.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<channel", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRss(targetId, text);
        }

        return ParseHtml(targetId, text);
    }

    internal static OfficialStatus ParseStatuspage(string targetId, string json) => ParseBody(targetId, json);

    internal static OfficialStatus ParseHtml(string targetId, string html)
    {
        var text = Normalize(html);
        if (ContainsAny(text,
                "we're fully operational",
                "we're not aware of any issues",
                "all systems operational",
                "fully operational",
                "service fully operational",
                "no incidents declared",
                "not actively mitigating"))
        {
            if (!ContainsAny(text, "we're currently experiencing issues", "elevated errors", "partial outage"))
            {
                return Ok(targetId);
            }
        }

        if (ContainsAny(text, "major outage", "critical outage", "service disruption"))
        {
            return new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Down, Label = "官方故障" };
        }

        if (ContainsAny(text,
                "we're currently experiencing issues",
                "experiencing issues",
                "degraded performance",
                "partial outage",
                "elevated errors"))
        {
            return new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Degraded, Label = "官方降级" };
        }

        return Unknown(targetId);
    }

    internal static OfficialStatus FromIndicator(string targetId, string indicator) =>
        indicator.Trim().ToLowerInvariant() switch
        {
            "critical" or "major" or "major_outage" or "down" or "outage" =>
                new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Down, Label = "官方故障" },
            "minor" or "maintenance" or "degraded" or "degraded_performance" or "partial_outage" or "warn" =>
                new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Degraded, Label = "官方降级" },
            "none" or "operational" or "ok" or "success" or "available" or "up" =>
                Ok(targetId),
            "error" or "unknown" or "insufficient_data" or "" =>
                Unknown(targetId),
            _ => Unknown(targetId)
        };

    internal static OfficialStatus ParseRss(string targetId, string xml)
    {
        var active = false;
        var severe = false;
        foreach (Match match in RssItem.Matches(xml))
        {
            var item = match.Value;
            var status = RssStatus.Match(item);
            if (!status.Success)
            {
                continue;
            }

            var value = status.Groups[1].Value.ToUpperInvariant();
            if (value is "RESOLVED")
            {
                continue;
            }

            active = true;
            if (value is "CRITICAL" or "MAJOR")
            {
                severe = true;
            }
        }

        if (severe)
        {
            return new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Down, Label = "官方故障" };
        }

        if (active)
        {
            return new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Degraded, Label = "官方降级" };
        }

        return Ok(targetId);
    }

    private async Task<OfficialStatus> ReadOneAsync(
        ProbeTarget target,
        Task<IReadOnlyDictionary<string, OfficialStatus>> hubTask,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(target.Id, out var cached))
            {
                var age = DateTimeOffset.Now - cached.At;
                var ttl = cached.Value.Level is OfficialLevel.Unknown ? MissTtl : FreshTtl;
                if (age < ttl)
                {
                    return cached.Value;
                }
            }
        }

        foreach (var url in UrlsFor(target.Id))
        {
            var fetched = await FetchAsync(url, cancellationToken);
            if (fetched is null)
            {
                continue;
            }

            var parsed = ParseBody(target.Id, fetched);
            if (parsed.Level is not OfficialLevel.Unknown)
            {
                return Store(target.Id, parsed);
            }
        }

        IReadOnlyDictionary<string, OfficialStatus> hub;
        try
        {
            hub = await hubTask;
        }
        catch
        {
            hub = new Dictionary<string, OfficialStatus>(StringComparer.OrdinalIgnoreCase);
        }

        if (hub.TryGetValue(target.Id, out var fromHub) && fromHub.Level is not OfficialLevel.Unknown)
        {
            return Store(target.Id, fromHub);
        }

        return Store(target.Id, Unknown(target.Id));
    }

    private async Task<IReadOnlyDictionary<string, OfficialStatus>> ReadHubAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, OfficialStatus>(StringComparer.OrdinalIgnoreCase);
        var body = await FetchAsync("https://study8677.github.io/ai-status-hub/last_run.json", cancellationToken);
        if (body is null)
        {
            return map;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("services", out var services) ||
                services.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (var service in services.EnumerateArray())
            {
                var name = service.TryGetProperty("service", out var serviceName)
                    ? serviceName.GetString() ?? ""
                    : "";
                var id = HubId(name);
                if (id is null)
                {
                    continue;
                }

                var level = service.TryGetProperty("level", out var levelEl) ? levelEl.GetString() ?? "" : "";
                var overall = service.TryGetProperty("overall_status", out var overallEl) ? overallEl.GetString() ?? "" : "";
                var parsed = FromIndicator(id, string.IsNullOrWhiteSpace(level) ? overall : level);
                if (parsed.Level is OfficialLevel.Unknown)
                {
                    parsed = FromIndicator(id, overall);
                }

                if (parsed.Level is not OfficialLevel.Unknown)
                {
                    map[id] = parsed;
                }
            }
        }
        catch (JsonException)
        {
            // Hub is a fallback; ignore a broken snapshot.
        }

        return map;
    }

    private async Task<string?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AttemptTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return body;
        }
        catch
        {
            return null;
        }
    }

    private OfficialStatus Store(string id, OfficialStatus value)
    {
        lock (_gate)
        {
            _cache[id] = (DateTimeOffset.Now, value);
        }

        return value;
    }

    private static OfficialStatus ParseJson(string targetId, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseJsonElement(targetId, document.RootElement);
        }
        catch (JsonException)
        {
            return Unknown(targetId);
        }
    }

    private static OfficialStatus ParseJsonElement(string targetId, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return ParseGoogleIncidents(targetId, root);
        }

        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("official_status", out var official) &&
                official.ValueKind == JsonValueKind.Object &&
                official.TryGetProperty("status", out var officialStatus))
            {
                var parsedOfficial = FromIndicator(targetId, officialStatus.GetString() ?? "");
                if (parsedOfficial.Level is not OfficialLevel.Unknown)
                {
                    return parsedOfficial;
                }
            }

            if (result.TryGetProperty("status", out var resultStatus))
            {
                var parsed = FromIndicator(targetId, resultStatus.GetString() ?? "");
                if (parsed.Level is not OfficialLevel.Unknown)
                {
                    return parsed;
                }
            }
        }

        if (root.TryGetProperty("status", out var status))
        {
            if (status.ValueKind == JsonValueKind.Object &&
                status.TryGetProperty("indicator", out var indicator))
            {
                return FromIndicator(targetId, indicator.GetString() ?? "none");
            }

            if (status.ValueKind == JsonValueKind.String)
            {
                var parsed = FromIndicator(targetId, status.GetString() ?? "");
                if (parsed.Level is not OfficialLevel.Unknown)
                {
                    return parsed;
                }
            }
        }

        if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            var worst = OfficialLevel.Ok;
            var any = false;
            foreach (var component in components.EnumerateArray())
            {
                if (!component.TryGetProperty("status", out var componentStatus))
                {
                    continue;
                }

                any = true;
                var level = FromIndicator(targetId, componentStatus.GetString() ?? "").Level;
                if (level is OfficialLevel.Down)
                {
                    worst = OfficialLevel.Down;
                }
                else if (level is OfficialLevel.Degraded && worst is not OfficialLevel.Down)
                {
                    worst = OfficialLevel.Degraded;
                }
            }

            if (any)
            {
                return worst switch
                {
                    OfficialLevel.Down => new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Down, Label = "官方故障" },
                    OfficialLevel.Degraded => new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Degraded, Label = "官方降级" },
                    _ => Ok(targetId)
                };
            }
        }

        if (root.TryGetProperty("ongoing_incidents", out var incidents) && incidents.ValueKind == JsonValueKind.Array)
        {
            return incidents.GetArrayLength() == 0
                ? Ok(targetId)
                : new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Degraded, Label = "官方降级" };
        }

        if (root.TryGetProperty("services", out var services) && services.ValueKind == JsonValueKind.Array)
        {
            foreach (var service in services.EnumerateArray())
            {
                var name = service.TryGetProperty("service", out var serviceName) ? serviceName.GetString() ?? "" : "";
                if (!string.Equals(HubId(name), targetId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var level = service.TryGetProperty("level", out var levelEl) ? levelEl.GetString() ?? "" : "";
                var overall = service.TryGetProperty("overall_status", out var overallEl) ? overallEl.GetString() ?? "" : "";
                var parsed = FromIndicator(targetId, string.IsNullOrWhiteSpace(level) ? overall : level);
                if (parsed.Level is OfficialLevel.Unknown)
                {
                    parsed = FromIndicator(targetId, overall);
                }

                return parsed;
            }
        }

        return Unknown(targetId);
    }

    private static OfficialStatus ParseGoogleIncidents(string targetId, JsonElement incidents)
    {
        if (!targetId.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return Unknown(targetId);
        }

        var worst = OfficialLevel.Ok;
        foreach (var incident in incidents.EnumerateArray())
        {
            if (incident.TryGetProperty("end", out var end) &&
                end.ValueKind is not JsonValueKind.Null &&
                end.ValueKind is not JsonValueKind.Undefined &&
                !string.IsNullOrWhiteSpace(end.ToString()) &&
                end.ToString() is not "null")
            {
                continue;
            }

            var blob = incident.GetRawText();
            if (!ContainsAny(blob, GeminiNeedles))
            {
                continue;
            }

            worst = ContainsAny(blob, "high", "critical", "outage")
                ? OfficialLevel.Down
                : OfficialLevel.Degraded;
            if (worst is OfficialLevel.Down)
            {
                break;
            }
        }

        return worst switch
        {
            OfficialLevel.Down => new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Down, Label = "官方故障" },
            OfficialLevel.Degraded => new OfficialStatus { TargetId = targetId, Level = OfficialLevel.Degraded, Label = "官方降级" },
            _ => Ok(targetId)
        };
    }

    private static IEnumerable<string> ExtractJsonBlobs(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            yield return trimmed;
            yield break;
        }

        var objectStart = trimmed.IndexOf('{');
        var arrayStart = trimmed.IndexOf('[');
        var start = objectStart >= 0 && (arrayStart < 0 || objectStart < arrayStart) ? objectStart : arrayStart;
        if (start < 0)
        {
            yield break;
        }

        yield return trimmed[start..];
    }

    private static string? HubId(string name) => name.Trim().ToLowerInvariant() switch
    {
        "openai" or "chatgpt" or "codex" => "codex",
        "claude" or "anthropic" => "claude",
        "gemini" or "google" or "google ai" => "gemini",
        "grok" or "xai" or "x.ai" or "spacexai" => "grok",
        "cursor" => "cursor",
        _ => null
    };

    private static string Normalize(string text) =>
        text.Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"');

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static OfficialStatus Ok(string id) =>
        new() { TargetId = id, Level = OfficialLevel.Ok, Label = "官方正常" };

    private static OfficialStatus Unknown(string id) =>
        new() { TargetId = id, Level = OfficialLevel.Unknown, Label = "官方未知" };

    public void Dispose() => _http.Dispose();
}
