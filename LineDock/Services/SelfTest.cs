using System.Windows.Media;
using LineDock.Models;
using LineDock.Native;

namespace LineDock.Services;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            Require(ClientDetector.MatchesTnt("tntcloudLite"), "tnt process name");
            Require(ClientDetector.MatchesYunyun("YunyunCore"), "yunyun core process name");
            Require(ClientDetector.IdentifyProcess("Clash Verge") == "Clash Verge", "clash verge fingerprint");
            Require(ClientDetector.IdentifyProcess("clash-verge-rev") == "Clash Verge", "clash verge rev fingerprint");
            Require(ClientDetector.IdentifyProcess("v2rayN") == "v2rayN", "v2rayn fingerprint");
            Require(ClientDetector.IdentifyProcess("mihomo") == "Mihomo", "mihomo fingerprint");
            Require(ClientDetector.IdentifyProcess("FlClash") == "FlClash", "flclash fingerprint");
            Require(ClientDetector.IdentifyProcess("letsvpn") == "LetsVPN", "letsvpn fingerprint");
            Require(ClientDetector.IdentifyProcess("lantern") == "蓝灯", "lantern fingerprint");
            Require(ClientDetector.IsHttpProxyPort(7890) && ClientDetector.IsSocksProxyPort(10808), "common proxy ports");
            var picked = ClientDetector.PickLocalProxy([(10808, 11), (7890, 12)], [(12, "clash")]);
            Require(picked is { Port: 7890, Socks: false, Pid: 12 }, "prefer http mixed port over socks");
            var custom = ClientDetector.PickLocalProxy([(38472, 44)], [(44, "clash-verge-rev")]);
            Require(custom is { Port: 38472, Pid: 44 }, "known client on unknown port still counts");
            var github = AppUpdate.ParseGitHubRelease("""
                {"tag_name":"v0.2.0","html_url":"https://github.com/Nixz0824/AiDocks/releases/tag/v0.2.0","assets":[{"name":"LineDock.exe","browser_download_url":"https://github.com/Nixz0824/AiDocks/releases/download/v0.2.0/LineDock.exe"}]}
                """);
            Require(github is { Version: "0.2.0" } && github.Url.EndsWith("LineDock.exe", StringComparison.Ordinal), "github release parser");
            Require(new ClientPresence { DiscoveredProxy = "127.0.0.1:7890" }.AnyClient, "unknown client with local proxy is still a path");
            Require(new ClientPresence { TunnelUp = true }.AnyClient, "tunnel counts as a path");
            Require(TcpOwners.PortFromNetworkOrder(0x1234) == 0x3412, "tcp port byte order");

            var ids = ProbeCatalog.Normalize(["cursor", "nope", "grok"]);
            Require(ids.SequenceEqual(["grok", "cursor"]), "catalog order and drop unknown");
            Require(ProbeCatalog.Normalize(["cf", "google"]).SequenceEqual(ProbeCatalog.DefaultIds), "legacy ids fall back");
            Require(ProbeCatalog.Overseas.Any(target => target.Id == "workbuddy-cn") &&
                    ProbeCatalog.Overseas.Any(target => target.Id == "workbuddy-intl"), "WorkBuddy CN/intl probes");
            Require(ProbeCatalog.Overseas.All(target => target.Id != "chatgpt"), "plus menu has no duplicate ChatGPT");
            Require(ProbeCatalog.Normalize(["chatgpt", "grok"]).SequenceEqual(["grok", "codex"]), "chatgpt maps onto codex");

            Require(LineScoring.FromLatency(90) == LineStatus.Healthy, "90ms healthy");
            Require(LineScoring.FromLatency(200) == LineStatus.Fair, "200ms fair");
            Require(LineScoring.FromLatency(418) == LineStatus.Unstable, "418ms unstable not healthy");
            Require(LineScoring.FromLatency(900) == LineStatus.Poor, "900ms poor");
            Require(LineScoring.Connect(true, false, false, null) == LineStatus.DirectBlocked, "direct blocked");
            Require(LineScoring.Connect(true, false, true, null) == LineStatus.ClientIdle, "client idle");
            Require(LineScoring.Connect(false, false, false, null) == LineStatus.Offline, "offline");

            var green = LineScoring.ColorFor(LineStatus.Healthy);
            var orange = LineScoring.ColorFor(LineStatus.Unstable);
            var red = LineScoring.ColorFor(LineStatus.Poor);
            Require(green.G > green.R, "healthy is green");
            Require(orange.R > orange.G && orange.G > 80, "unstable is orange");
            Require(red.R > red.G, "poor is red");
            Require(LineScoring.PipColor(LineStatus.Unstable, 418).Equals(orange), "pip matches card color");

            var worst = LineScoring.Worst(
            [
                new EndpointResult { Id = "grok", Label = "Grok", Overseas = true, Ok = true, LatencyMs = 418 },
                new EndpointResult { Id = "codex", Label = "Codex", Overseas = true, Ok = true, LatencyMs = 952 }
            ], true, true);
            Require(worst.Id == "codex" && worst.Status == LineStatus.Poor, "pip follows worst AI line");

            var page = OfficialStatusClient.ParseStatuspage("codex", """{"status":{"indicator":"none","description":"All Systems Operational"}}""");
            Require(page.Level == OfficialLevel.Ok && page.Label == "官方正常", "statuspage none");
            var down = OfficialStatusClient.ParseStatuspage("codex", """{"status":{"indicator":"major","description":"Partial outage"}}""");
            Require(down.Level == OfficialLevel.Down, "statuspage major");
            var xai = OfficialStatusClient.ParseHtml("grok", "No incidents declared. We are not actively mitigating.");
            Require(xai.Level == OfficialLevel.Ok, "xai no incidents");
            var openai = OfficialStatusClient.ParseHtml("codex", "We're fully operational. We're not aware of any issues affecting our systems.");
            Require(openai.Level == OfficialLevel.Ok, "openai incident.io operational");
            var curly = OfficialStatusClient.ParseHtml("codex", "We\u2019re fully operational. We\u2019re not aware of any issues affecting our systems.");
            Require(curly.Level == OfficialLevel.Ok, "curly apostrophe still operational");
            var empty = OfficialStatusClient.ParseHtml("gemini", "<!doctype html><html></html>");
            Require(empty.Level == OfficialLevel.Unknown, "empty html is unknown not ok");
            var rssOk = OfficialStatusClient.ParseRss("grok", """
                <rss><channel><item><description><![CDATA[<h3>Status: RESOLVED</h3>]]></description>
                <category>resolved</category></item></channel></rss>
                """);
            Require(rssOk.Level == OfficialLevel.Ok, "resolved rss is ok");
            var rssDown = OfficialStatusClient.ParseRss("grok", """
                <rss><channel><item><description><![CDATA[<h3>Status: INVESTIGATING</h3>]]></description>
                </item></channel></rss>
                """);
            Require(rssDown.Level == OfficialLevel.Degraded, "investigating rss is degraded");
            var model = OfficialStatusClient.ParseBody("codex", """{"state":"success","result":{"status":"operational","official_status":{"status":"operational"}}}""");
            Require(model.Level == OfficialLevel.Ok, "modelstatus operational");
            var jinaJson = OfficialStatusClient.ParseBody("cursor", """
                Title: Cursor
                Markdown Content:
                {"status":{"indicator":"none","description":"All Systems Operational"}}
                """);
            Require(jinaJson.Level == OfficialLevel.Ok, "json inside jina markdown");
            var geminiOk = OfficialStatusClient.ParseBody("gemini", """[{"id":"1","end":"2026-09-01T18:52:00+00:00","external_desc":"Gemini outage last week"}]""");
            Require(geminiOk.Level == OfficialLevel.Ok, "closed gemini incident is ok");
            var geminiDown = OfficialStatusClient.ParseBody("gemini", """[{"id":"2","end":null,"external_desc":"Gemini API outage","severity":"high"}]""");
            Require(geminiDown.Level is OfficialLevel.Down or OfficialLevel.Degraded, "open gemini incident is not ok");

            var draft = new ProbeSnapshot
            {
                Client = new ClientPresence { TntRunning = true, SystemProxy = true, DisplayName = "tntcloud Lite", PathLabel = "系统代理" },
                Endpoints =
                [
                    new EndpointResult { Id = "grok", Label = "Grok", Overseas = true, Ok = true, LatencyMs = 88 },
                    new EndpointResult { Id = "codex", Label = "Codex", Overseas = true, Ok = true, LatencyMs = 110 },
                    new EndpointResult { Id = "cn", Label = "国内", Overseas = false, Ok = true, LatencyMs = 24 }
                ],
                PublicIp = "203.0.113.9",
                Location = "JP",
                DirectOverseasOk = false
            };
            var classified = LineScoring.Classify(draft, []);
            Require(classified.PipSourceId == "codex", "classify pip source");
            Require(classified.PipStatus == LineStatus.Healthy, "both healthy pip");

            Require(CrashLog.Redact("Authorization: Bearer abcdefghijklmnop").Contains("[redacted]"), "crash log redacts bearer");
            Require(!CrashLog.Redact("token=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.aaaaaaaabbbbbbbb.ccccccccdddddddd").Contains("eyJ"), "crash log redacts jwt");
            Require(LineCopy.StatusTitle(LineStatus.Unstable, "Grok") == "Grok偏慢", "slow title");
            Require(Geometry.Parse(RailGeometry.BuildPath(272, 0)).Bounds.Width > 0, "rail path");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"self-test failed: {name}");
        }
    }
}
