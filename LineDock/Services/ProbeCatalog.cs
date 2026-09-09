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
        new("cursor", "Cursor", "cursor.com", 443, "https://cursor.com", true, "https://status.cursor.com/api/v2/summary.json"),
        new("kimi-cn", "Kimi 国内", "kimi.com", 443, "https://www.kimi.com", false),
        new("kimi-intl", "Kimi 国际", "platform.kimi.ai", 443, "https://platform.kimi.ai", true),
        new("workbuddy-cn", "WorkBuddy 国内", "www.workbuddy.cn", 443, "https://www.workbuddy.cn", false),
        new("workbuddy-intl", "WorkBuddy 国际", "www.workbuddy.ai", 443, "https://www.workbuddy.ai", true),
        new("glm-cn", "GLM 国内", "open.bigmodel.cn", 443, "https://open.bigmodel.cn", false),
        new("glm-intl", "GLM 国际", "z.ai", 443, "https://z.ai", true),
        new("qoder-cn", "Qoder 国内", "qoder.com.cn", 443, "https://qoder.com.cn", false),
        new("qoder-intl", "Qoder 国际", "qoder.com", 443, "https://qoder.com", true),
        new("trae-cn", "Trae 国内", "www.trae.cn", 443, "https://www.trae.cn", false),
        new("trae-intl", "Trae 国际", "www.trae.ai", 443, "https://www.trae.ai", true),
        new("minimax-cn", "MiniMax 国内", "www.minimaxi.com", 443, "https://www.minimaxi.com", false),
        new("minimax-intl", "MiniMax 国际", "www.minimax.io", 443, "https://www.minimax.io", true)
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
