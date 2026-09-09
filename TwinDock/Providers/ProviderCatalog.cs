using TwinDock.Models;
using System.Net.Http;

namespace TwinDock.Providers;

internal sealed record ProviderDefinition(
    string Id,
    string DisplayName,
    string Glyph,
    string AccentHex,
    string LoginHint,
    bool HasLiveQuota);

internal static class ProviderCatalog
{
    public static readonly string[] DefaultIds = ["codex", "grok"];

    public static IReadOnlyList<ProviderDefinition> All { get; } =
    [
        new("codex", "Codex Usage", "⌬", "#20E3A2", "请先登录 Codex Desktop 或 ChatGPT Desktop，或运行 codex login。", true),
        new("opencode", "OpenCode Usage", "▣", "#E4E4E4", "请先运行 opencode auth login 选择 OpenCode Go，或在 opencode.ai/auth 复制 API Key。", true),
        new("claude", "Claude Usage", "✶", "#D97757", "请先登录 Claude Desktop 或 Claude Code。", true),
        new("grok", "Grok Usage", "×", "#FF5A36", "请先运行 grok login。", true),
        new("cursor", "Cursor Usage", "▸", "#F4F4F5", "请先登录 Cursor 桌面应用。", true),
        new("gemini", "Gemini Usage", "✦", "#8AB4F8", "请先登录 Gemini CLI，或登录 Google AI Studio。", true),
        ..DomesticSpecs.All.Select(spec => new ProviderDefinition(spec.Id, spec.DisplayName, spec.Glyph, spec.AccentHex, spec.LoginHint, true))
    ];

    public static IReadOnlyList<IQuotaProvider> CreateProviders(HttpClient httpClient)
    {
        return All.Select(definition => (IQuotaProvider)(definition.Id switch
        {
            "codex" => new CodexQuotaProvider(httpClient),
            "opencode" => new OpenCodeQuotaProvider(httpClient),
            "claude" => new ClaudeQuotaProvider(httpClient),
            "grok" => new GrokQuotaProvider(httpClient),
            "cursor" => new CursorQuotaProvider(httpClient),
            "gemini" => new GeminiQuotaProvider(httpClient),
            _ => (IQuotaProvider?)DomesticSpecs.Create(httpClient, definition.Id) ?? new StubQuotaProvider(definition)
        })).ToArray();
    }

    public static HashSet<string> Normalize(IEnumerable<string>? ids)
    {
        var known = All.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new List<string>();
        foreach (var id in ids ?? [])
        {
            var mapped = id.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) ? "codex" : id;
            if (known.Contains(mapped) && !selected.Contains(mapped, StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(mapped);
            }
        }

        if (selected.Count == 0)
        {
            selected.AddRange(DefaultIds);
        }

        return selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Ordered(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(item => set.Contains(item.Id)).Select(item => item.Id).ToArray();
    }
}

internal sealed class StubQuotaProvider(ProviderDefinition definition) : IQuotaProvider
{
    public string Id => definition.Id;
    public string DisplayName => definition.DisplayName;
    public string Glyph => definition.Glyph;
    public string AccentHex => definition.AccentHex;

    public Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult.Missing(this, definition.LoginHint));
}
