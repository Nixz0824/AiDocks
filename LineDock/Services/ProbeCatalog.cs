using LineDock.Models;

namespace LineDock.Services;

internal static class ProbeCatalog
{
    public static readonly ProbeTarget[] Overseas =
    [
        new("grok", "Grok", "grok.com", 443, "https://grok.com", true, "https://status.x.ai/feed.xml"),
        new("codex", "Codex", "chatgpt.com", 443, "https://chatgpt.com", true, "https://modelstatus.ai/api/provider/openai/status"),
        new("claude", "Claude", "claude.ai", 443, "https://claude.ai", true, "https://status.anthropic.com/api/v2/status.json"),
        new("gemini", "Gemini", "gemini.google.com", 443, "https://gemini.google.com", true, "https://status.cloud.google.com/incidents.json"),
        new("cursor", "Cursor", "cursor.com", 443, "https://cursor.com", true, "https://status.cursor.com/api/v2/summary.json")
    ];

    public static readonly ProbeTarget Mainland = new("cn", "国内", "www.baidu.com", 443, null, false);

    public static IReadOnlyList<string> DefaultIds { get; } = ["grok", "codex"];

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? ids)
    {
        var set = new HashSet<string>(
            (ids ?? DefaultIds).Select(id => id.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) ? "codex" : id),
            StringComparer.OrdinalIgnoreCase);
        var ordered = Overseas.Select(target => target.Id).Where(set.Contains).ToList();
        return ordered.Count == 0 ? DefaultIds : ordered;
    }

    public static IEnumerable<ProbeTarget> Selected(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(Normalize(ids), StringComparer.OrdinalIgnoreCase);
        return Overseas.Where(target => set.Contains(target.Id));
    }

    public static ProbeTarget? Find(string id) =>
        Overseas.FirstOrDefault(target => target.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
