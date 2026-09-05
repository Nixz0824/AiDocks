namespace QuotaDock.Services;

public enum DockEdge
{
    Left,
    Right
}

public sealed class AppSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Right;
    public double VerticalOffset { get; set; } = 0.28;
    public int RefreshMinutes { get; set; } = 2;
    public List<string> EnabledProviderIds { get; set; } = ["codex", "grok"];
}
