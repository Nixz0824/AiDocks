using System.Text;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuotaDock.Models;
using QuotaDock.Providers;

namespace QuotaDock.Services;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            var codexCredentials = CodexQuotaProvider.ParseCredentials("""
                {"tokens":{"access_token":"secret","account_id":"acct"}}
                """);
            Require(codexCredentials is { AccessToken: "secret", AccountId: "acct" }, "Codex credentials parser");

            using var codexStream = JsonStream("""
                {"rate_limit":{"primary_window":{"used_percent":41,"limit_window_seconds":18000,"reset_at":1788292800},"secondary_window":{"used_percent":18,"limit_window_seconds":604800,"reset_at":1788724800}}}
                """);
            var codex = CodexQuotaProvider.ParseUsageAsync(codexStream, CancellationToken.None).GetAwaiter().GetResult();
            Require(codex.Count == 2 && codex[0].Label == "5 小时限额" && codex[1].Label == "周限额" && Math.Abs(codex[1].UsedPercent - 18) < 0.01, "Codex usage parser");

            var appServer = CodexAppServerClient.ParseRateLimitResponse("""
                {"id":2,"result":{"rateLimits":{"primary":{"usedPercent":40,"windowDurationMins":300,"resetsAt":1788338492},"secondary":{"usedPercent":79,"windowDurationMins":10080,"resetsAt":1788753950}}}}
                """);
            Require(appServer is { Count: 2 } && appServer[0].Label == "5 小时限额" && appServer[1].Label == "周限额", "Codex app-server parser");

            var grokToken = GrokQuotaProvider.SelectXaiToken("""
                {"https://company.example::client":{"oidc_issuer":"https://company.example","key":"wrong"},"https://auth.x.ai::client":{"oidc_issuer":"https://auth.x.ai","key":"right"}}
                """);
            Require(grokToken == "right", "Grok issuer boundary");

            using var grokStream = JsonStream("""
                {"config":{"creditUsagePercent":73,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_MONTHLY","start":"2026-08-01T00:00:00Z","end":"2026-09-01T00:00:00Z"}}}
                """);
            var grok = GrokQuotaProvider.ParseUsageAsync(grokStream, CancellationToken.None).GetAwaiter().GetResult();
            Require(grok.Count == 1 && grok[0].Label == "订阅周期" && Math.Abs(grok[0].UsedPercent - 73) < 0.01, "Grok billing parser");

            using var cursorStream = JsonStream("""
                {"billingCycleStart":"1787382451548","billingCycleEnd":"1790060851548","planUsage":{"autoPercentUsed":3,"apiPercentUsed":12.5}}
                """);
            var cursor = CursorQuotaProvider.ParseUsageAsync(cursorStream, CancellationToken.None).GetAwaiter().GetResult();
            Require(cursor.Count == 2 && cursor[0].Label == "包含用量" && cursor[1].Label == "API 用量" && Math.Abs(cursor[1].UsedPercent - 12.5) < 0.01, "Cursor usage parser");

            using var claudeStream = JsonStream("""
                {"five_hour":{"utilization":7,"resets_at":"2026-09-03T16:00:00Z"},"seven_day":{"utilization":42,"resets_at":"2026-09-07T16:00:00Z"}}
                """);
            var claude = ClaudeQuotaProvider.ParseUsageAsync(claudeStream, CancellationToken.None).GetAwaiter().GetResult();
            Require(claude.Count == 2 && claude[0].Label == "5 小时限额" && Math.Abs(claude[1].UsedPercent - 42) < 0.01, "Claude usage parser");

            using var geminiStream = JsonStream("""
                {"buckets":[{"remainingFraction":0.78,"resetTime":"2026-09-04T00:00:00Z"},{"remainingFraction":0.5,"resetTime":"2026-09-04T00:00:00Z"}]}
                """);
            var gemini = GeminiQuotaProvider.ParseUsageAsync(geminiStream, CancellationToken.None).GetAwaiter().GetResult();
            Require(gemini.Count == 1 && gemini[0].Label == "每日限额" && Math.Abs(gemini[0].UsedPercent - 50) < 0.01, "Gemini usage parser");

            var opencodeKey = OpenCodeQuotaProvider.SelectApiKey("""
                {"opencode-go":{"type":"api","key":"sk-go"},"opencode":{"type":"api","key":"sk-legacy"}}
                """);
            Require(opencodeKey == "sk-go", "OpenCode Go key priority");
            var opencodeLegacy = OpenCodeQuotaProvider.SelectApiKey("""
                {"opencode":{"type":"api","key":"sk-legacy"}}
                """);
            Require(opencodeLegacy == "sk-legacy", "OpenCode legacy key fallback");
            var opencodeConfig = OpenCodeQuotaProvider.SelectApiKeyFromConfig("""
                {"provider":{"opencode-go":{"options":{"apiKey":"sk-cfg"}}}}
                """);
            Require(opencodeConfig == "sk-cfg", "OpenCode config key");

            var fixedNow = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
            using var opencodeStream = JsonStream("""
                {"useBalance":false,"rollingUsage":{"status":"ok","resetInSec":14400,"usagePercent":42},"weeklyUsage":{"status":"ok","resetInSec":259200,"usagePercent":17},"monthlyUsage":{"status":"ok","resetInSec":1728000,"usagePercent":8}}
                """);
            var opencode = OpenCodeQuotaProvider.ParseUsageAsync(opencodeStream, CancellationToken.None, fixedNow).GetAwaiter().GetResult();
            Require(opencode.Count == 3 && opencode[0].Label == "5 小时滚动" && Math.Abs(opencode[0].UsedPercent - 42) < 0.01, "OpenCode usage parser");
            Require(opencode[1].Label == "周限额" && opencode[2].Label == "月限额", "OpenCode window labels");
            Require(opencode[0].ResetsAt == fixedNow.AddSeconds(14400), "OpenCode reset time");

            using var opencodeSnake = JsonStream("""
                {"rollingUsage":{"status":"ok","reset_in_seconds":60,"usage_percent":5}}
                """);
            var opencodeSnakeParsed = OpenCodeQuotaProvider.ParseUsageAsync(opencodeSnake, CancellationToken.None, fixedNow).GetAwaiter().GetResult();
            Require(opencodeSnakeParsed.Count == 1 && Math.Abs(opencodeSnakeParsed[0].UsedPercent - 5) < 0.01, "OpenCode snake_case tolerance");

            var opencodeGeometry = Geometry.Parse("F0 M20,22 H4 V2 H20 V22 Z M16,18 H8 V6 H16 V18 Z");
            Require(!opencodeGeometry.Bounds.IsEmpty, "OpenCode official mark");


            var cursorGeometry = Geometry.Parse("F1 M11.503,0.131 L1.891,5.678 A0.84,0.84 0 0 0 1.471,6.404 L1.471,17.592 A0.84,0.84 0 0 0 1.891,18.316 L11.5,23.866 A1,1 0 0 0 12.498,23.866 L22.108,18.316 A0.84,0.84 0 0 0 22.528,17.592 L22.528,6.404 A0.84,0.84 0 0 0 22.108,5.678 L12.497,0.131 A1.01,1.01 0 0 0 11.501,0.131 Z M2.657,6.338 L21.207,6.338 C21.47,6.338 21.637,6.625 21.504,6.853 L12.23,22.918 C12.168,23.025 12.001,22.982 12.001,22.858 L12.001,12.335 A0.59,0.59 0 0 0 11.706,11.825 L2.596,6.568 C2.487,6.505 2.532,6.338 2.657,6.338 Z");
            Require(!cursorGeometry.Bounds.IsEmpty, "Cursor official mark");

            Require(ClientDetector.IdentifyProcess("clash-verge-rev") == "Clash Verge", "clash verge fingerprint");
            Require(ClientDetector.IdentifyProcess("letsvpn") == "LetsVPN", "letsvpn fingerprint");
            var picked = ClientDetector.PickLocalProxy([(38472, 44)], [(44, "clash-verge-rev")]);
            Require(picked is { Port: 38472, Pid: 44 }, "known client on unknown port still counts");
            var github = AppUpdate.ParseGitHubRelease("""
                {"tag_name":"v0.2.0","html_url":"https://github.com/Nixz0824/AiDocks/releases/tag/v0.2.0","assets":[{"name":"QuotaDock.exe","browser_download_url":"https://github.com/Nixz0824/AiDocks/releases/download/v0.2.0/QuotaDock.exe"}]}
                """);
            Require(github is { Version: "0.2.0" } && github.Url.EndsWith("QuotaDock.exe", StringComparison.Ordinal), "github release parser");

            Require(ProviderCatalog.All.Select(item => item.Id).Distinct().Count() == ProviderCatalog.All.Count, "Provider catalog ids");
            var stub = new StubQuotaProvider(ProviderCatalog.All.First(item => item.Id == "claude"));
            var missing = stub.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            Require(missing.State == ProviderState.MissingCredentials && missing.ProviderId == "claude", "Stub login state");

            var fiveHour = new QuotaWindow("5 小时限额", 100, null, TimeSpan.FromHours(5));
            var weekly = new QuotaWindow("周限额", 70, null, TimeSpan.FromDays(7));
            var monthly = new QuotaWindow("订阅周期", 40, null, TimeSpan.FromDays(30));
            Require(Math.Abs(QuotaPresentation.Remaining(QuotaPresentation.HeadlineWindow([fiveHour, weekly])!.UsedPercent) - 30) < 0.01, "Weekly remaining headline");
            Require(Math.Abs(QuotaPresentation.Remaining(QuotaPresentation.HeadlineWindow([fiveHour, weekly, monthly])!.UsedPercent) - 60) < 0.01, "Monthly remaining headline");
            var included = new QuotaWindow("包含用量", 3, null, TimeSpan.FromDays(30));
            var api = new QuotaWindow("API 用量", 0, null, TimeSpan.FromDays(30));
            Require(Math.Abs(QuotaPresentation.Remaining(QuotaPresentation.HeadlineWindow([included, api])!.UsedPercent) - 97) < 0.01, "Cursor included remaining headline");

            foreach (var morph in new[] { 0d, 0.25d, 0.5d, 0.75d, 1d })
            {
                var geometry = Geometry.Parse(RailGeometry.BuildPath(272, morph));
                Require(!geometry.Bounds.IsEmpty, $"Rail path morph {morph}");
            }

            using var invalidJson = JsonStream("{");
            var emptyWindows = CodexQuotaProvider.ParseUsageAsync(invalidJson, CancellationToken.None).GetAwaiter().GetResult();
            Require(emptyWindows.Count == 0, "Invalid Codex JSON");

            var frozen = new ScaleTransform(1, 1);
            frozen.Freeze();
            var clone = frozen.Clone();
            Require(!clone.IsFrozen, "Clone unfreezes ScaleTransform");
            clone.BeginAnimation(ScaleTransform.ScaleXProperty, null);

            Require(QuotaDock.App.IsRecoverableUiException(new InvalidOperationException("该对象被冻结。")), "Recoverable frozen transform");
            Require(!QuotaDock.App.IsRecoverableUiException(new InvalidOperationException("unexpected")), "Non-recoverable invalid operation");
            Require(!SingleInstanceCoordinator.IsQuotaDockProduct(@"C:\missing-quotadock.exe"), "Missing file is not QuotaDock");
            Require(!SingleInstanceCoordinator.IsQuotaDockProduct(@"C:\Windows\System32\notepad.exe"), "Notepad is not QuotaDock");

            var cachePath = Path.Combine(Path.GetTempPath(), $"quotadock-cache-{Guid.NewGuid():N}.json");
            try
            {
                var cache = new QuotaCacheStore(cachePath);
                var ready = new QuotaSnapshot("codex", "Codex Usage", "⌬", "#20E3A2", [new QuotaWindow("周限额", 42, DateTimeOffset.Now.AddDays(2), TimeSpan.FromDays(7))], DateTimeOffset.Now, ProviderState.Ready);
                cache.MergeAndSave([ready]);
                var stale = cache.MergeAndSave([ProviderResult.Unavailable(new TestProvider(), "网络不可用")]);
                Require(stale.Count == 1 && stale[0].State == ProviderState.Stale && stale[0].Windows.Count == 1, "Last-good quota cache");
            }
            finally
            {
                try
                {
                    File.Delete(cachePath);
                }
                catch
                {
                    // Test cleanup is best-effort.
                }
            }
            var streamOrder = new List<string>();
            var streamCachePath = Path.Combine(Path.GetTempPath(), $"quotadock-stream-{Guid.NewGuid():N}.json");
            try
            {
                var slow = new DelayedProvider("grok", "Grok Usage", "×", "#FF5A36", 220);
                var fast = new DelayedProvider("codex", "Codex Usage", "⌬", "#20E3A2", 10);
                using var streamService = new QuotaService([fast, slow], new QuotaCacheStore(streamCachePath));
                // 自检跑在 UI 线程（Dispatcher 同步上下文），直接阻塞等含真正异步
                // （Task.Delay）的 RefreshAsync 会死锁；扔到线程池上等。
                var streamed = Task.Run(() => streamService.RefreshAsync(false, ["codex", "grok"], CancellationToken.None, (snapshot, _) =>
                {
                    streamOrder.Add(snapshot.ProviderId);
                    return Task.CompletedTask;
                })).GetAwaiter().GetResult();
                Require(streamOrder.Count == 2 && streamOrder[0] == "codex", "Streaming reports fast provider first");
                Require(streamed.Count == 2 && streamed[0].ProviderId == "codex" && streamed[1].ProviderId == "grok", "Streaming keeps catalog order");
            }
            finally
            {
                try
                {
                    File.Delete(streamCachePath);
                }
                catch
                {
                    // Test cleanup is best-effort.
                }
            }
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static MemoryStream JsonStream(string json) => new(Encoding.UTF8.GetBytes(json));

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {name}");
        }
    }

    private sealed class TestProvider : IQuotaProvider
    {
        public string Id => "codex";
        public string DisplayName => "Codex Usage";
        public string Glyph => "⌬";
        public string AccentHex => "#20E3A2";
        public Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DelayedProvider(string id, string displayName, string glyph, string accent, int delayMs) : IQuotaProvider
    {
        public string Id => id;
        public string DisplayName => displayName;
        public string Glyph => glyph;
        public string AccentHex => accent;

        public async Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(delayMs, cancellationToken);
            return new QuotaSnapshot(id, displayName, glyph, accent,
                [new QuotaWindow("订阅周期", 10, null, TimeSpan.FromDays(30))], DateTimeOffset.Now, ProviderState.Ready);
        }
    }
}
