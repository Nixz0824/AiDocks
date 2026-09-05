namespace TwinDock.Models;

public enum ProviderState
{
    Ready,
    Stale,
    MissingCredentials,
    AuthenticationRequired,
    Unavailable,
    Loading
}

public sealed record QuotaWindow(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? Duration);

public sealed record QuotaSnapshot(
    string ProviderId,
    string DisplayName,
    string Glyph,
    string AccentHex,
    IReadOnlyList<QuotaWindow> Windows,
    DateTimeOffset UpdatedAt,
    ProviderState State,
    string? StatusMessage = null)
{
    public double SummaryPercent => Windows.Count == 0 ? 0 : Windows.Max(window => window.UsedPercent);
}
