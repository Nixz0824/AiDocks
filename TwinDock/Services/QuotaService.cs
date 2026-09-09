using TwinDock.Models;
using TwinDock.Providers;
using System.Net.Http;

namespace TwinDock.Services;

public sealed class QuotaService : IDisposable
{
    private readonly HttpClient _httpClient = OutboundHttp.Create(TimeSpan.FromSeconds(15), "TwinDock/0.2");
    private readonly IReadOnlyList<IQuotaProvider> _providers;
    private readonly QuotaCacheStore _cache;

    public QuotaService()
    {
        _providers = ProviderCatalog.CreateProviders(_httpClient);
        _cache = new QuotaCacheStore();
    }

    internal QuotaService(IReadOnlyList<IQuotaProvider> providers, QuotaCacheStore cache)
    {
        _providers = providers;
        _cache = cache;
    }

    public IReadOnlyList<QuotaSnapshot> LoadingSnapshots(IEnumerable<string> enabledIds)
    {
        var enabled = ProviderCatalog.Normalize(enabledIds);
        return _cache.InitialSnapshots(Selected(enabled));
    }

    public async Task<IReadOnlyList<QuotaSnapshot>> RefreshAsync(bool demoMode, IEnumerable<string> enabledIds, CancellationToken cancellationToken, Func<QuotaSnapshot, CancellationToken, Task>? onSnapshot = null)
    {
        var selected = Selected(ProviderCatalog.Normalize(enabledIds));
        if (demoMode)
        {
            await Task.Delay(180, cancellationToken);
            var demos = DemoSnapshots(selected.Select(provider => provider.Id));
            var mergedDemos = new List<QuotaSnapshot>(demos.Count);
            foreach (var demo in demos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var merged = _cache.MergeAndSaveOne(demo);
                mergedDemos.Add(merged);
                if (onSnapshot is not null)
                {
                    await onSnapshot(merged, cancellationToken);
                    await Task.Delay(60, cancellationToken);
                }
            }

            return mergedDemos;
        }

        // 流式回填：谁先回来谁先上屏，不等最慢的一家。
        var pending = selected.Select(provider => (provider, task: provider.FetchAsync(cancellationToken))).ToList();
        var mergedById = new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending.Select(entry => entry.task));
            var index = pending.FindIndex(entry => entry.task == done);
            var (provider, _) = pending[index];
            pending.RemoveAt(index);
            QuotaSnapshot fresh;
            try
            {
                fresh = await done;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 各 Provider 内部已把可预期失败转成 Unavailable，这里只防第三方实现抛异常。
                fresh = ProviderResult.Unavailable(provider, "暂时无法读取");
            }

            var merged = _cache.MergeAndSaveOne(fresh);
            mergedById[merged.ProviderId] = merged;
            if (onSnapshot is not null)
            {
                await onSnapshot(merged, cancellationToken);
            }
        }

        return selected.Select(provider => mergedById[provider.Id]).ToArray();
    }

    public static IReadOnlyList<QuotaSnapshot> DemoSnapshots(IEnumerable<string>? enabledIds = null)
    {
        var now = DateTimeOffset.Now;
        var enabled = ProviderCatalog.Normalize(enabledIds ?? ProviderCatalog.DefaultIds);
        var demos = new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new("codex", "Codex Usage", "⌬", "#20E3A2",
                [new QuotaWindow("5 小时限额", 41, now.AddMinutes(51), TimeSpan.FromHours(5)), new QuotaWindow("周限额", 18, now.AddDays(3).AddHours(7), TimeSpan.FromDays(7))],
                now, ProviderState.Ready),
            ["opencode"] = new("opencode", "OpenCode Usage", "▣", "#E4E4E4",
                [new QuotaWindow("5 小时滚动", 42, now.AddHours(4), TimeSpan.FromHours(5)), new QuotaWindow("周限额", 17, now.AddDays(3), TimeSpan.FromDays(7)), new QuotaWindow("月限额", 8, now.AddDays(21), TimeSpan.FromDays(30))],
                now, ProviderState.Ready),
            ["claude"] = new("claude", "Claude Usage", "✶", "#D97757",
                [new QuotaWindow("5 小时限额", 62, now.AddHours(3), TimeSpan.FromHours(5)), new QuotaWindow("周限额", 28, now.AddDays(4), TimeSpan.FromDays(7))],
                now, ProviderState.Ready),
            ["grok"] = new("grok", "Grok Usage", "×", "#FF5A36",
                [new QuotaWindow("订阅周期", 73, now.AddDays(9).AddHours(2), TimeSpan.FromDays(30))],
                now, ProviderState.Ready),
            ["cursor"] = new("cursor", "Cursor Usage", "▸", "#F4F4F5",
                [new QuotaWindow("包含用量", 34, now.AddDays(5), TimeSpan.FromDays(30)), new QuotaWindow("API 用量", 67, now.AddDays(5), TimeSpan.FromDays(30))],
                now, ProviderState.Ready),
            ["gemini"] = new("gemini", "Gemini Usage", "✦", "#8AB4F8",
                [new QuotaWindow("每日限额", 22, now.AddHours(11), TimeSpan.FromHours(24))],
                now, ProviderState.Ready)
        };

        return ProviderCatalog.All.Where(item => enabled.Contains(item.Id)).Select(item =>
            demos.TryGetValue(item.Id, out var demo)
                ? demo
                : new QuotaSnapshot(item.Id, item.DisplayName, item.Glyph, item.AccentHex,
                    [new QuotaWindow("订阅周期", 28, now.AddDays(14), TimeSpan.FromDays(30))],
                    now, ProviderState.Ready)).ToArray();
    }

    private IReadOnlyList<IQuotaProvider> Selected(HashSet<string> enabled) =>
        _providers.Where(provider => enabled.Contains(provider.Id)).ToArray();

    public void Dispose() => _httpClient.Dispose();
}
