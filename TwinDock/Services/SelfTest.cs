using System.Text;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TwinDock.Models;
using TwinDock.Providers;

namespace TwinDock.Services;

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
            Require(TwinDock.Controls.BrandIcon.AllMarksParse(), "domestic official marks parse");

            Require(ClientDetector.IdentifyProcess("clash-verge-rev") == "Clash Verge", "clash verge fingerprint");
            Require(ClientDetector.IdentifyProcess("letsvpn") == "LetsVPN", "letsvpn fingerprint");
            var picked = ClientDetector.PickLocalProxy([(38472, 44)], [(44, "clash-verge-rev")]);
            Require(picked is { Port: 38472, Pid: 44 }, "known client on unknown port still counts");
            var github = AppUpdate.ParseGitHubRelease("""
                {"tag_name":"v0.2.0","html_url":"https://github.com/Nixz0824/AiDocks/releases/tag/v0.2.0","assets":[{"name":"TwinDock.exe","browser_download_url":"https://github.com/Nixz0824/AiDocks/releases/download/v0.2.0/TwinDock.exe"}]}
                """);
            Require(github is { Version: "0.2.0" } && github.Url.EndsWith("TwinDock.exe", StringComparison.Ordinal), "github release parser");

            Require(ProviderCatalog.All.Select(item => item.Id).Distinct().Count() == ProviderCatalog.All.Count, "Provider catalog ids");
            Require(ProviderCatalog.All.Any(item => item.Id == "kimi") &&
                    ProviderCatalog.All.Any(item => item.Id == "workbuddy") &&
                    ProviderCatalog.All.Any(item => item.Id == "glm") &&
                    ProviderCatalog.All.Any(item => item.Id == "qoder") &&
                    ProviderCatalog.All.Any(item => item.Id == "trae") &&
                    ProviderCatalog.All.Any(item => item.Id == "minimax"), "domestic providers present");
            Require(ProviderCatalog.All.All(item => !item.Id.EndsWith("-cn", StringComparison.Ordinal) &&
                                                    !item.Id.EndsWith("-intl", StringComparison.Ordinal)),
                "domestic region split removed (one entry per brand)");
            var legacyIds = ProviderCatalog.Normalize(["kimi-cn", "workbuddy-intl", "codex"]);
            Require(legacyIds.Contains("kimi") && legacyIds.Contains("workbuddy") && legacyIds.Contains("codex") &&
                    legacyIds.Count == 3, "legacy region ids collapse onto brand ids");

            // Payload fixtures are the real shapes the vendors' own clients send (see ATTRIBUTION.md).
            var workbuddy = DomesticProviders.Parse("workbuddy", """
                {"code":0,"msg":"OK","data":{"Packages":[{"PackageCode":"p1","CycleTotalCapacity":"1600","CycleRemainCapacity":"1600","CycleUsedCapacity":"0","CapacityUnit":"credits"},{"PackageCode":"p2","CycleTotalCapacity":"500","CycleRemainCapacity":"488.58","CycleUsedCapacity":"11.42","CapacityUnit":"credits"}]}}
                """);
            Require(workbuddy.Windows.Count == 2, "WorkBuddy package count");
            Require(Math.Abs(workbuddy.Windows[0].UsedPercent - 0) < 0.01, "WorkBuddy untouched package");
            Require(Math.Abs(workbuddy.Windows[1].UsedPercent - 2.284) < 0.01, "WorkBuddy cycle percent");

            var glm = DomesticProviders.Parse("glm", """
                {"code":200,"msg":"success","data":{"limits":[{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":12.5,"nextResetTime":1788935000000},{"type":"TOKENS_LIMIT","unit":6,"number":1,"percentage":4.2,"nextResetTime":1789500000000},{"type":"TIME_LIMIT","unit":6,"number":1,"percentage":80}]}}
                """);
            Require(glm.Windows.Count == 2, "GLM ignores the MCP TIME_LIMIT row");
            Require(glm.Windows.Any(window => window.Label == "5 小时限额" && Math.Abs(window.UsedPercent - 12.5) < 0.01), "GLM 5h window");
            Require(glm.Windows.Any(window => window.Label == "周限额" && Math.Abs(window.UsedPercent - 4.2) < 0.01), "GLM weekly window");

            var kimi = DomesticProviders.Parse("kimi", """
                {"usage":{"limit":1000,"used":250,"remaining":750,"resetTime":"2026-09-16T00:00:00Z"},"limits":[{"window":{"duration":300,"timeUnit":"MINUTE"},"detail":{"limit":100,"remaining":88,"resetTime":1788935000000}}],"totalQuota":{"limit":5000,"remaining":4100}}
                """);
            Require(kimi.Windows.Count == 3, "Kimi three windows");
            Require(kimi.Windows.Any(window => window.Label == "5 小时限额" && Math.Abs(window.UsedPercent - 12) < 0.01), "Kimi 5h window");
            Require(kimi.Windows.Any(window => window.Label == "周限额" && Math.Abs(window.UsedPercent - 25) < 0.01), "Kimi weekly window");
            Require(kimi.Windows.Any(window => window.Label == "总订阅额度" && Math.Abs(window.UsedPercent - 18) < 0.01), "Kimi total credits window");
            Require(kimi.Windows.First(window => window.Label == "5 小时限额").ResetsAt ==
                    DateTimeOffset.FromUnixTimeMilliseconds(1788935000000), "Kimi 5h reset from detail.resetTime");
            Require(kimi.Windows.First(window => window.Label == "周限额").ResetsAt is not null, "Kimi weekly reset from usage.resetTime");

            var minimax = DomesticProviders.Parse("minimax", """
                {"base_resp":{"status_code":0,"status_msg":"success"},"model_remains":[{"model_name":"MiniMax-M2","current_interval_total_count":300,"current_interval_usage_count":270,"current_weekly_total_count":2000,"current_weekly_usage_count":1500}]}
                """);
            Require(minimax.Windows.Count == 2, "MiniMax window count");
            Require(Math.Abs(minimax.Windows.First(window => window.Label == "5 小时限额").UsedPercent - 10) < 0.01, "MiniMax remaining-not-used semantics");
            Require(Math.Abs(minimax.Windows.First(window => window.Label == "周限额").UsedPercent - 25) < 0.01, "MiniMax weekly percent");
            var minimaxAuth = DomesticProviders.Parse("minimax", """
                {"base_resp":{"status_code":1004,"status_msg":"cookie is missing, log in again"}}
                """);
            Require(minimaxAuth.AuthFailed, "MiniMax auth failure surfaces");

            // Real /v1/token_plan/remains payload (MiniMax Token Plan): percentages carry the data and
            // the counts are 0, so a count-only parser would produce nothing. end_time /
            // weekly_end_time are the reset timestamps.
            var minimaxTokenPlan = DomesticProviders.Parse("minimax", """
                {"model_remains":[{"start_time":1781834400000,"end_time":1781852400000,"remains_time":16640796,"current_interval_total_count":0,"current_interval_usage_count":0,"model_name":"general","current_weekly_total_count":0,"current_weekly_usage_count":0,"weekly_start_time":1781452800000,"weekly_end_time":1782057600000,"weekly_remains_time":221840796,"current_interval_status":1,"current_interval_remaining_percent":98,"current_weekly_status":1,"current_weekly_remaining_percent":67},{"start_time":1781798400000,"end_time":1781884800000,"model_name":"video","current_interval_remaining_percent":100,"current_weekly_status":1,"current_weekly_remaining_percent":100}],"base_resp":{"status_code":0,"status_msg":"success"}}
                """);
            Require(minimaxTokenPlan.Windows.Count == 2, "MiniMax Token Plan windows come from the percentages");
            var tokenPlanInterval = minimaxTokenPlan.Windows.First(window => window.Label == "5 小时限额");
            Require(Math.Abs(tokenPlanInterval.UsedPercent - 2) < 0.01, "MiniMax Token Plan interval percent");
            Require(tokenPlanInterval.ResetsAt == DateTimeOffset.FromUnixTimeMilliseconds(1781852400000), "MiniMax interval reset from end_time");
            Require(minimaxTokenPlan.Windows.First(window => window.Label == "周限额").ResetsAt ==
                    DateTimeOffset.FromUnixTimeMilliseconds(1782057600000), "MiniMax weekly reset from weekly_end_time");
            var minimaxNoWeekly = DomesticProviders.Parse("minimax", """
                {"model_remains":[{"model_name":"MiniMax-M2","current_interval_remaining_percent":50,"current_weekly_status":0}],"base_resp":{"status_code":0,"status_msg":"success"}}
                """);
            Require(minimaxNoWeekly.Windows.Count == 1, "MiniMax weekly window skipped when the plan has none");

            var qoder = DomesticProviders.Parse("qoder", """
                {"usageType":"plan","totalUsagePercentage":23.5,"isQuotaExceeded":false,"expiresAt":1790060851548,"userQuota":{"total":1000,"used":235,"remaining":765},"addOnQuota":{"total":200,"used":0,"remaining":200}}
                """);
            Require(qoder.Windows.Count == 2, "Qoder plan + add-on rows");
            Require(Math.Abs(qoder.Windows.First(window => window.Label == "基础额度").UsedPercent - 23.5) < 0.01, "Qoder plan percent");
            Require(Math.Abs(qoder.Windows.First(window => window.Label == "赠送额度").UsedPercent - 0) < 0.01, "Qoder add-on percent");
            Require(qoder.Windows.All(window => window.ResetsAt == DateTimeOffset.FromUnixTimeMilliseconds(1790060851548)), "Qoder plan expiry carried onto every row");

            var trae = DomesticProviders.Parse("trae", """
                {"is_credits_billing":false,"user_entitlement_pack_list":[{"entitlement_base_info":{"product_type":1,"status":1,"is_hide":false,"quota":{"basic_usage_limit":500,"bonus_usage_limit":100},"end_time":1790000000000},"usage":{"basic_usage_amount":125,"bonus_usage_amount":20},"display_desc":"Pro"},{"entitlement_base_info":{"product_type":3,"quota":{"basic_usage_limit":100}},"usage":{"basic_usage_amount":10}}]}
                """);
            Require(trae.Windows.Count == 1, "Trae skips promo packs");
            Require(trae.Windows[0].Label == "Pro" && Math.Abs(trae.Windows[0].UsedPercent - 21.55) < 0.02, "Trae bonus-aware percent");
            Require(trae.Windows[0].ResetsAt is not null, "Trae pack end time");
            // Live-captured WorkBuddy payloads (2026-09-09): the summary carries the numbers,
            // the resource endpoint carries the package names and cycle end dates.
            using (var summaryDocument = JsonDocument.Parse("""
                       {"code":0,"msg":"OK","data":{"Packages":[{"PackageCode":"TCACA_code_007_nzdH5h4Nl0","CycleTotalCapacity":"1600","CycleRemainCapacity":"1600","CycleUsedCapacity":"0","CapacityUnit":"credits"},{"PackageCode":"TCACA_code_008_cfWoLwvjU4","CycleTotalCapacity":"500","CycleRemainCapacity":"488.58","CycleUsedCapacity":"11.42","CapacityUnit":"credits"}],"IsPaidUser":false}}
                       """))
            using (var resourceDocument = JsonDocument.Parse("""
                       {"code":0,"msg":"OK","data":{"Response":{"Data":{"Accounts":[{"PackageCode":"TCACA_code_008_cfWoLwvjU4","PackageName":"CodeBuddy个人体验版","SubProductName":"腾讯云代码助手 (IDE)","CapacitySize":500,"CycleCapacitySize":500,"CycleCapacityUsed":11,"CycleEndTime":"2026-09-30 23:59:59","ExpiredTime":""},{"PackageCode":"TCACA_code_007_nzdH5h4Nl0","PackageName":"CodeBuddy个人版国内运营裂变包","SubProductName":"腾讯云代码助手 (IDE) - 赠送包","CapacitySize":1500,"CycleCapacitySize":1500,"CycleEndTime":"2026-10-09 13:27:37"},{"PackageCode":"TCACA_code_007_nzdH5h4Nl0","PackageName":"CodeBuddy个人版国内运营裂变包","SubProductName":"腾讯云代码助手 (IDE) - 赠送包","CapacitySize":100,"CycleCapacitySize":100,"CycleEndTime":"2026-10-09 13:27:53"}]}}}}
                       """))
            {
                var merged = DomesticProviders.WorkBuddy(summaryDocument.RootElement, resourceDocument.RootElement);
                Require(merged.Windows.Count == 2, "WorkBuddy merge keeps two packages");
                var plan = merged.Windows.First(window => window.Label == "订阅额度");
                var bonus = merged.Windows.First(window => window.Label == "奖励额度");
                Require(Math.Abs(plan.UsedPercent - 2.284) < 0.01, "WorkBuddy plan percent uses the precise summary value");
                Require(plan.ResetsAt is { } planReset && planReset.Year == 2026 && planReset.Month == 9 && planReset.Day == 30,
                    "WorkBuddy plan reset comes from the resource cycle end");
                Require(Math.Abs(bonus.UsedPercent) < 0.01, "WorkBuddy bonus percent");
                Require(bonus.ResetsAt is { } bonusReset && bonusReset.Month == 10 && bonusReset.Day == 9,
                    "WorkBuddy bonus keeps the earliest expiry of the aggregated grant rows");
            }

            // Credential discovery against the real file shapes the vendors' apps write.
            Require(LocalCredential.Extract("""{"auth":{"accessToken":"tok-a"}}""") == "tok-a", "credential extract: nested auth.accessToken");
            Require(LocalCredential.Extract("""{"api_key":"sk-x"}""") == "sk-x", "credential extract: api_key");
            Require(LocalCredential.Extract("api_key = \"sk-toml\"\nbase_url = \"x\"") == "sk-toml", "credential extract: toml api_key");
            Require(LocalCredential.Extract("  sk-plain  ") == "sk-plain", "credential extract: plain key file");

            // Full provider pipeline against a loopback HTTP stub: credential discovery → request
            // construction → status handling → parser. Only TLS/DNS and the vendor's real server are
            // outside this test; those are covered by the live endpoint probe.
            Environment.SetEnvironmentVariable("AIDOCKS_SELFTEST_TOKEN", "tok-selftest");
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                using (var wbStub = new LoopbackStub(200, """
                           {"code":0,"msg":"OK","data":{"Packages":[{"CycleTotalCapacity":"500","CycleRemainCapacity":"488.58","CycleUsedCapacity":"11.42"}]}}
                           """))
                {
                    var spec = new DomesticSpec(
                        "workbuddy", "WorkBuddy", "▣", "#2A9D8F", "hint",
                        ["AIDOCKS_SELFTEST_TOKEN"], [],
                        [new DomesticEndpoint($"http://127.0.0.1:{wbStub.Port}/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}""")],
                        DomesticProviders.ParserFor("workbuddy"));
                    var snapshot = Fetch(new DomesticQuotaProvider(client, spec));
                    Require(snapshot.State == ProviderState.Ready && snapshot.Windows.Count == 1 &&
                            Math.Abs(snapshot.Windows[0].UsedPercent - 2.284) < 0.01, "WorkBuddy pipeline reaches Ready over HTTP");
                    Require(wbStub.LastRequestLine?.StartsWith("POST /billing/meter/get-user-resource-summary", StringComparison.Ordinal) == true, "WorkBuddy request method and path");
                    Require(wbStub.LastHeaders.TryGetValue("Authorization", out var workbuddyAuth) && workbuddyAuth == "Bearer tok-selftest", "WorkBuddy bearer header");
                    Require(wbStub.LastBody?.Contains("p_tcaca", StringComparison.Ordinal) == true, "WorkBuddy request body");
                }

                using (var glmStub = new LoopbackStub(200, """
                           {"code":200,"msg":"success","data":{"limits":[{"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":42}]}}
                           """))
                {
                    var spec = new DomesticSpec(
                        "glm", "GLM", "▲", "#0F6FFF", "hint",
                        ["AIDOCKS_SELFTEST_TOKEN"], [],
                        [new DomesticEndpoint($"http://127.0.0.1:{glmStub.Port}/api/monitor/usage/quota/limit", Auth: DomesticAuthStyle.Bare)],
                        DomesticProviders.ParserFor("glm"));
                    var snapshot = Fetch(new DomesticQuotaProvider(client, spec));
                    Require(snapshot.State == ProviderState.Ready && Math.Abs(snapshot.Windows[0].UsedPercent - 42) < 0.01, "GLM pipeline reaches Ready over HTTP");
                    Require(glmStub.LastHeaders.TryGetValue("Authorization", out var glmAuth) && glmAuth == "tok-selftest", "GLM domestic host sends the bare key");
                }

                using (var unauthorizedStub = new LoopbackStub(401, """{"error":{"message":"Invalid Authentication"}}"""))
                {
                    var spec = new DomesticSpec(
                        "kimi", "Kimi", "◐", "#F5C518", "hint",
                        ["AIDOCKS_SELFTEST_TOKEN"], [],
                        [new DomesticEndpoint($"http://127.0.0.1:{unauthorizedStub.Port}/coding/v1/usages")],
                        DomesticProviders.ParserFor("kimi"));
                    var snapshot = Fetch(new DomesticQuotaProvider(client, spec));
                    Require(snapshot.State == ProviderState.AuthenticationRequired, "HTTP 401 maps to AuthenticationRequired");
                }

                using (var bodyAuthStub = new LoopbackStub(200, """{"base_resp":{"status_code":1004,"status_msg":"login fail"}}"""))
                {
                    var spec = new DomesticSpec(
                        "minimax", "MiniMax", "▬", "#E11D48", "hint",
                        ["AIDOCKS_SELFTEST_TOKEN"], [],
                        [new DomesticEndpoint($"http://127.0.0.1:{bodyAuthStub.Port}/v1/token_plan/remains")],
                        DomesticProviders.ParserFor("minimax"));
                    var snapshot = Fetch(new DomesticQuotaProvider(client, spec));
                    Require(snapshot.State == ProviderState.AuthenticationRequired, "MiniMax 200-with-auth-error maps to AuthenticationRequired");
                }

                var missingSpec = new DomesticSpec(
                    "kimi", "Kimi", "◐", "#F5C518", "请先运行 kimi login",
                    ["AIDOCKS_SELFTEST_ABSENT"], [],
                    [new DomesticEndpoint("http://127.0.0.1:1/coding/v1/usages")],
                    DomesticProviders.ParserFor("kimi"));
                var missingSnapshot = Fetch(new DomesticQuotaProvider(client, missingSpec));
                Require(missingSnapshot.State == ProviderState.MissingCredentials &&
                        missingSnapshot.StatusMessage == "请先运行 kimi login", "no credential maps to MissingCredentials with the login hint");
                // The two-call WorkBuddy flow end to end: summary for the numbers, resource for the
                // cycle end dates, merged into one snapshot.
                using (var workbuddySummaryStub = new LoopbackStub(200, """
                           {"code":0,"msg":"OK","data":{"Packages":[{"PackageCode":"TCACA_code_007_nzdH5h4Nl0","CycleTotalCapacity":"1600","CycleRemainCapacity":"1600","CycleUsedCapacity":"0"},{"PackageCode":"TCACA_code_008_cfWoLwvjU4","CycleTotalCapacity":"500","CycleRemainCapacity":"488.58","CycleUsedCapacity":"11.42"}]}}
                           """))
                using (var workbuddyResourceStub = new LoopbackStub(200, """
                           {"code":0,"msg":"OK","data":{"Response":{"Data":{"Accounts":[{"PackageCode":"TCACA_code_008_cfWoLwvjU4","PackageName":"CodeBuddy个人体验版","SubProductName":"腾讯云代码助手 (IDE)","CycleCapacitySize":500,"CycleCapacityUsed":11,"CycleEndTime":"2026-09-30 23:59:59"},{"PackageCode":"TCACA_code_007_nzdH5h4Nl0","PackageName":"CodeBuddy个人版国内运营裂变包","SubProductName":"腾讯云代码助手 (IDE) - 赠送包","CycleCapacitySize":1500,"CycleEndTime":"2026-10-09 13:27:37"}]}}}}
                           """))
                {
                    var provider = new WorkBuddyQuotaProvider(
                        client,
                        [new DomesticEndpoint($"http://127.0.0.1:{workbuddySummaryStub.Port}/billing/meter/get-user-resource-summary", Post: true, Body: """{"productCode":"p_tcaca"}""")],
                        new DomesticEndpoint($"http://127.0.0.1:{workbuddyResourceStub.Port}/v2/billing/meter/get-user-resource", Post: true, Body: """{"productCode":"p_tcaca"}"""));
                    var workbuddySnapshot = Fetch(provider);
                    Require(workbuddySnapshot.State == ProviderState.Ready && workbuddySnapshot.Windows.Count == 2, "WorkBuddy two-call pipeline reaches Ready");
                    Require(workbuddySnapshot.Windows.All(window => window.ResetsAt is not null), "WorkBuddy resets survive the two-call pipeline");
                    Require(workbuddySnapshot.Windows.Any(window => window.Label == "订阅额度" && window.ResetsAt!.Value.Day == 30 && window.ResetsAt.Value.Month == 9), "WorkBuddy plan reset in the snapshot");
                    Require(workbuddySnapshot.Windows.Any(window => window.Label == "奖励额度" && window.ResetsAt!.Value.Month == 10), "WorkBuddy bonus reset in the snapshot");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("AIDOCKS_SELFTEST_TOKEN", null);
            }


            Require(ProviderCatalog.All.All(item => item.Id != "chatgpt"), "plus menu has no duplicate ChatGPT");
            Require(ProbeCatalog.Overseas.All(target => target.Id != "chatgpt"), "line catalog has no duplicate ChatGPT");
            Require(ProviderCatalog.Normalize(["chatgpt", "grok"]).Contains("codex") &&
                    !ProviderCatalog.Normalize(["chatgpt", "grok"]).Contains("chatgpt"), "chatgpt maps onto codex");
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

            Require(TwinDock.App.IsRecoverableUiException(new InvalidOperationException("该对象被冻结。")), "Recoverable frozen transform");
            Require(!TwinDock.App.IsRecoverableUiException(new InvalidOperationException("unexpected")), "Non-recoverable invalid operation");
            Require(!SingleInstanceCoordinator.IsTwinDockProduct(@"C:\missing-quotadock.exe"), "Missing file is not TwinDock");
            Require(!SingleInstanceCoordinator.IsTwinDockProduct(@"C:\Windows\System32\notepad.exe"), "Notepad is not TwinDock");

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
        catch (Exception exception)
        {
            CrashLog.Write(exception);
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "twindock-selftest-failure.txt"),
                    exception.ToString());
            }
            catch
            {
                // Reporting the failure is best-effort.
            }

            return 1;
        }
    }

    private static MemoryStream JsonStream(string json) => new(Encoding.UTF8.GetBytes(json));

    /// <summary>Runs a provider fetch off the dispatcher thread so the loopback test cannot deadlock.</summary>
    private static QuotaSnapshot Fetch(IQuotaProvider provider) =>
        Task.Run(() => provider.FetchAsync(CancellationToken.None)).GetAwaiter().GetResult();

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




