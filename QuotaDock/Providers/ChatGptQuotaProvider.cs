using System.Net.Http;
using QuotaDock.Models;

namespace QuotaDock.Providers;

public sealed class ChatGptQuotaProvider(HttpClient httpClient) : IQuotaProvider
{
    private readonly CodexQuotaProvider _codex = new(httpClient);

    public string Id => "chatgpt";
    public string DisplayName => "ChatGPT Usage";
    public string Glyph => "⌬";
    public string AccentHex => "#10A37F";

    public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _codex.FetchAsync(cancellationToken);
        var message = snapshot.StatusMessage?
            .Replace("Codex Desktop 或 CLI", "ChatGPT Desktop 或 Codex", StringComparison.Ordinal)
            .Replace("Codex 登录文件无法读取", "ChatGPT 登录文件无法读取", StringComparison.Ordinal)
            .Replace("请重新登录 Codex", "请重新登录 ChatGPT 或 Codex", StringComparison.Ordinal)
            .Replace("Codex", "ChatGPT", StringComparison.Ordinal);
        return snapshot with
        {
            ProviderId = Id,
            DisplayName = DisplayName,
            Glyph = Glyph,
            AccentHex = AccentHex,
            StatusMessage = message
        };
    }
}
