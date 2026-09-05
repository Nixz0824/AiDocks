namespace LineDock.Models;

public enum LineStatus
{
    Probing,
    Offline,
    DirectBlocked,
    ClientIdle,
    Poor,
    Unstable,
    Fair,
    Healthy
}

public enum OfficialLevel
{
    Unknown,
    Ok,
    Degraded,
    Down
}

public sealed record ProbeTarget(
    string Id,
    string Label,
    string Host,
    int Port,
    string? HttpUrl,
    bool Overseas,
    string? StatusUrl = null);

public sealed class OfficialStatus
{
    public required string TargetId { get; init; }
    public required OfficialLevel Level { get; init; }
    public required string Label { get; init; }
}

public sealed class EndpointResult
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required bool Overseas { get; init; }
    public required bool Ok { get; init; }
    public required int LatencyMs { get; init; }
}

public sealed class ClientPresence
{
    public bool TntRunning { get; init; }
    public bool YunyunRunning { get; init; }
    public bool TunnelUp { get; init; }
    public bool SystemProxy { get; init; }
    public bool HasKnownClient { get; init; }
    public string? ProxyServer { get; init; }
    public string? DiscoveredProxy { get; init; }
    public bool DiscoveredSocks { get; init; }
    public string DisplayName { get; init; } = "未检测到客户端";
    public string PathLabel { get; init; } = "直连";
    public bool AnyClient =>
        HasKnownClient || TntRunning || YunyunRunning || TunnelUp || SystemProxy ||
        !string.IsNullOrWhiteSpace(DiscoveredProxy);
}

public sealed class ProbeSnapshot
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public ClientPresence Client { get; init; } = new();
    public IReadOnlyList<EndpointResult> Endpoints { get; init; } = [];
    public string? PublicIp { get; init; }
    public string? Location { get; init; }
    public bool? DirectOverseasOk { get; init; }
    public bool MainlandOk { get; init; }
    public bool OverseasOk { get; init; }
    public int? OverseasLatencyMs { get; init; }
    public int? MainlandLatencyMs { get; init; }
    public double LossPercent { get; init; }
    public int JitterMs { get; init; }
    public double Health { get; init; }
    public LineStatus Status { get; init; } = LineStatus.Probing;
    public LineStatus PipStatus { get; init; } = LineStatus.Probing;
    public int? PipLatencyMs { get; init; }
    public string PipSourceId { get; init; } = "";
    public string PipSourceLabel { get; init; } = "";
    public IReadOnlyDictionary<string, OfficialStatus> Official { get; init; } =
        new Dictionary<string, OfficialStatus>(StringComparer.OrdinalIgnoreCase);
}

public static class LineCopy
{
    public static string StatusTitle(LineStatus status, string name) => status switch
    {
        LineStatus.Probing => $"{name}探测中",
        LineStatus.Offline => "网络离线",
        LineStatus.DirectBlocked => $"{name}阻断",
        LineStatus.ClientIdle => $"{name}未通",
        LineStatus.Poor => $"{name}很慢",
        LineStatus.Unstable => $"{name}偏慢",
        LineStatus.Fair => $"{name}轻微抖动",
        LineStatus.Healthy => $"{name}畅通",
        _ => name
    };

    public static string Summary(LineStatus status, int? latencyMs) => status switch
    {
        LineStatus.Probing => "…",
        LineStatus.Offline => "离线",
        LineStatus.DirectBlocked => "阻断",
        LineStatus.ClientIdle => "未通",
        _ when latencyMs is int ms => $"{ms}ms",
        _ => "—"
    };

    public static string LocationName(string? code) => code?.ToUpperInvariant() switch
    {
        "JP" => "日本",
        "US" => "美国",
        "SG" => "新加坡",
        "HK" => "香港",
        "TW" => "台湾",
        "KR" => "韩国",
        "DE" => "德国",
        "GB" or "UK" => "英国",
        "NL" => "荷兰",
        "AU" => "澳大利亚",
        "CA" => "加拿大",
        "FR" => "法国",
        "IN" => "印度",
        "CN" => "中国",
        null or "" => "",
        var other => other
    };
}
