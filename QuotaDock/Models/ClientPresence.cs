namespace QuotaDock.Models;

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
