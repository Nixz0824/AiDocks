namespace LineDock.Services;

public enum DockEdge
{
    Left,
    Right
}

public sealed class AppSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Right;
    public double VerticalOffset { get; set; } = 0.38;
    public int IntervalSeconds { get; set; } = 10;
    public List<string> EnabledTargetIds { get; set; } = ["grok", "codex"];
}
