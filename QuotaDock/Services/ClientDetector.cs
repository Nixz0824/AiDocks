using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using QuotaDock.Models;
using QuotaDock.Native;
using Microsoft.Win32;

namespace QuotaDock.Services;

internal static class ClientDetector
{
    private static readonly (string Needle, string Label)[] Fingerprints =
    [
        ("tntcloudLiteCore", "tntcloud Lite"),
        ("tntcloudLite", "tntcloud Lite"),
        ("tntcloud", "tntcloud Lite"),
        ("YunyunCore", "云云"),
        ("Yunyun", "云云"),
        ("clash-verge-rev", "Clash Verge"),
        ("Clash Verge", "Clash Verge"),
        ("Clash-Verge", "Clash Verge"),
        ("clash-verge", "Clash Verge"),
        ("ClashVerge", "Clash Verge"),
        ("ClashNyanpasu", "Clash Nyanpasu"),
        ("nyanpasu", "Clash Nyanpasu"),
        ("Clash for Windows", "Clash"),
        ("ClashN", "ClashN"),
        ("FlClash", "FlClash"),
        ("clash-meta", "Clash Meta"),
        ("ClashMeta", "Clash Meta"),
        ("mihomo-party", "Mihomo Party"),
        ("MihomoParty", "Mihomo Party"),
        ("mihomo", "Mihomo"),
        ("clash-mini", "Clash"),
        ("ClashMini", "Clash"),
        ("clash", "Clash"),
        ("v2rayN", "v2rayN"),
        ("v2rayn", "v2rayN"),
        ("v2rayA", "v2rayA"),
        ("v2raya", "v2rayA"),
        ("v2rayW", "V2RayW"),
        ("v2ray", "V2Ray"),
        ("xray", "Xray"),
        ("GUI.for.sing-box", "sing-box"),
        ("GUIForSingBox", "sing-box"),
        ("sing-box", "sing-box"),
        ("singbox", "sing-box"),
        ("NekoRay", "NekoRay"),
        ("nekoray", "NekoRay"),
        ("NekoBox", "NekoBox"),
        ("HiddifyNext", "Hiddify"),
        ("hiddify", "Hiddify"),
        ("hysteria", "Hysteria"),
        ("ss-local", "Shadowsocks"),
        ("shadowsocks", "Shadowsocks"),
        ("ssr-local", "Shadowsocks"),
        ("outline", "Outline"),
        ("wireguard", "WireGuard"),
        ("tailscale", "Tailscale"),
        ("CloudflareWARP", "Cloudflare WARP"),
        ("warp-svc", "Cloudflare WARP"),
        ("openvpn", "OpenVPN"),
        ("proxifier", "Proxifier"),
        ("sstap", "SSTAP"),
        ("Netch", "Netch"),
        ("naiveproxy", "NaiveProxy"),
        ("trojan", "Trojan"),
        ("Qv2ray", "Qv2ray"),
        ("qv2ray", "Qv2ray"),
        ("karing", "Karing"),
        ("letsvpn", "LetsVPN"),
        ("LetsVPN", "LetsVPN"),
        ("LetsPRO", "LetsVPN"),
        ("lantern", "蓝灯"),
        ("psiphon", "Psiphon"),
        ("astrill", "Astrill"),
        ("ExpressVPN", "ExpressVPN"),
        ("NordVPN", "NordVPN"),
        ("surfshark", "Surfshark"),
        ("mullvad", "Mullvad"),
        ("ProtonVPN", "Proton VPN"),
        ("Windscribe", "Windscribe"),
        ("SoftEther", "SoftEther"),
        ("ZeroTier", "ZeroTier"),
        ("tun2socks", "tun2socks"),
        ("leigod", "雷神加速器"),
        ("xunyou", "迅游"),
        ("UUGame", "网易UU")
    ];

    private static readonly int[] HttpPorts =
    [
        7890, 7891, 7892, 7893, 7897, 10809, 10810, 20171, 20172, 6152, 6154,
        8118, 8888, 8889, 8080, 2080, 2081, 12334, 12335, 2801, 2802, 10080,
        18080, 18081, 33210, 15732, 2334, 10800
    ];

    private static readonly int[] SocksPorts =
        [1080, 10808, 1081, 1082, 1086, 1087, 20170, 6153, 10808, 7890, 1085, 10801];

    private static readonly HashSet<int> IgnorePorts =
    [
        3000, 3001, 4173, 5173, 5174, 5432, 5672, 5900, 6379, 8000, 8001, 8081, 8082,
        8090, 8443, 9000, 9090, 9222, 9229, 11434, 1433, 1521, 27017, 3306, 3389,
        5357, 6463, 7680, 27036, 50321, 19000, 19001
    ];

    private static readonly HashSet<string> IgnoreProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "msedgewebview2", "firefox", "code", "devenv", "python", "pythonw",
        "node", "steam", "discord", "telegram", "wechat", "weixin", "qq", "explorer", "svchost",
        "idle", "system", "lsass", "services", "wininit", "csrss", "dwm", "searchhost",
        "runtimebroker", "fontdrvhost", "conhost", "powershell", "pwsh", "windowsterminal",
        "openconsole", "cmd", "dllhost", "taskhostw", "sihost", "widgets", "startmenuexperiencehost",
        "shellexperiencehost", "applicationframehost", "textinputhost", "securityhealthservice",
        "msmpeng", "searchindexer", "winword", "excel", "powerpnt", "outlook", "teams", "slack",
        "grok", "twindock", "linedock", "quotadock", "cursor", "windsurf"
    };

    private static readonly string[] TunnelHints =
    [
        "tap", "tun", "wintun", "wireguard", "clash", "meta", "sing-box", "singbox",
        "verykuai", "cloudflare warp", "warp", "tailscale", "zerotier", "sstap",
        "outline", "tun2socks", "wintun", "meta tun", "letsvpn", "nordlynx", "proton",
        "openvpn", "tap-windows", "mihomo", "netch", "hiddify", "lantern"
    ];

    private static readonly object CacheLock = new();
    private static ClientPresence? _cached;
    private static long _cachedUntil;

    public static ClientPresence Peek()
    {
        var now = Environment.TickCount64;
        lock (CacheLock)
        {
            if (_cached is not null && now < _cachedUntil)
            {
                return _cached;
            }

            _cached = Detect();
            _cachedUntil = now + 3000;
            return _cached;
        }
    }

    public static ClientPresence Detect()
    {
        var processes = SafeProcesses();
        var tunnel = TunnelAdapterUp();
        var (proxyOn, proxyServer, pac) = ReadSystemProxy();
        var envProxy = ReadEnvProxy();
        var listeners = TcpOwners.LoopbackListeners();
        var discovered = PickLocalProxy(listeners, processes);
        var display = ActiveClientName(processes, listeners, discovered, proxyOn, proxyServer, tunnel, pac, envProxy);
        var tnt = display.Contains("tntcloud", StringComparison.OrdinalIgnoreCase);
        var yunyun = display.Contains("云云", StringComparison.Ordinal);

        var path = BuildPathLabel(tunnel, proxyOn, proxyServer, pac, envProxy, discovered);
        return new ClientPresence
        {
            TntRunning = tnt,
            YunyunRunning = yunyun,
            TunnelUp = tunnel,
            SystemProxy = proxyOn || pac || !string.IsNullOrWhiteSpace(envProxy),
            HasKnownClient = display is not "未检测到代理" and not "系统代理" and not "本地代理"
                and not "隧道网卡" and not "环境变量代理",
            ProxyServer = proxyServer ?? envProxy,
            DiscoveredProxy = discovered is { } proxy ? $"{proxy.Host}:{proxy.Port}" : null,
            DiscoveredSocks = discovered?.Socks == true,
            DisplayName = display,
            PathLabel = path
        };
    }

    internal static bool MatchesTnt(string processName) =>
        MatchFingerprints([processName]).Contains("tntcloud Lite");

    internal static bool MatchesYunyun(string processName) =>
        MatchFingerprints([processName]).Contains("云云");

    internal static string? IdentifyProcess(string processName)
    {
        var labels = MatchFingerprints([processName]);
        return labels.Count == 0 ? null : labels[0];
    }

    internal static string ActiveClientName(
        IReadOnlyList<(int Pid, string Name)> processes,
        IReadOnlyList<(int Port, int Pid)> listeners,
        (string Host, int Port, bool Socks, int Pid)? discovered,
        bool proxyOn,
        string? proxyServer,
        bool tunnel,
        bool pac,
        string? envProxy = null)
    {
        var pid = discovered?.Pid ?? 0;
        if (pid <= 0 && proxyOn && TryParseProxyPort(proxyServer, out var port))
        {
            pid = listeners.FirstOrDefault(item => item.Port == port).Pid;
        }

        if (pid > 0)
        {
            var owner = processes.FirstOrDefault(item => item.Pid == pid);
            if (!string.IsNullOrWhiteSpace(owner.Name))
            {
                return IdentifyProcess(owner.Name) ?? owner.Name;
            }
        }

        var named = MatchFingerprints(processes.Select(item => item.Name));
        if (named.Count > 0)
        {
            return named[0];
        }

        if (tunnel)
        {
            return "隧道网卡";
        }

        if (proxyOn || pac)
        {
            return "系统代理";
        }

        if (!string.IsNullOrWhiteSpace(envProxy))
        {
            return "环境变量代理";
        }

        if (discovered is not null)
        {
            return "本地代理";
        }

        return "未检测到代理";
    }

    internal static bool TryParseProxyPort(string? server, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(server))
        {
            return false;
        }

        var value = server.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("socks5://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("socks://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("socks=", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https=", "", StringComparison.OrdinalIgnoreCase);
        var colon = value.LastIndexOf(':');
        return colon >= 0 && int.TryParse(value[(colon + 1)..], out port);
    }

    internal static bool LooksLikeTunnel(string name, string description)
    {
        var blob = $"{name} {description}";
        return TunnelHints.Any(hint => blob.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsHttpProxyPort(int port) => HttpPorts.Contains(port);

    internal static bool IsSocksProxyPort(int port) => SocksPorts.Contains(port);

    internal static (string Host, int Port, bool Socks, int Pid)? PickLocalProxy(
        IReadOnlyList<(int Port, int Pid)> listeners,
        IReadOnlyList<(int Pid, string Name)> processes)
    {
        var relevant = listeners
            .Where(item => item.Port > 0)
            .DistinctBy(item => item.Port)
            .ToArray();
        if (relevant.Length == 0)
        {
            return null;
        }

        var http = relevant.FirstOrDefault(item => IsHttpProxyPort(item.Port));
        if (http.Port != 0)
        {
            return ("127.0.0.1", http.Port, false, http.Pid);
        }

        var owned = relevant.FirstOrDefault(item =>
            item.Port is > 1024 and < 49152 &&
            !IgnorePorts.Contains(item.Port) &&
            IsProxyProcess(item.Pid, processes));
        if (owned.Port != 0)
        {
            return ("127.0.0.1", owned.Port, IsSocksProxyPort(owned.Port), owned.Pid);
        }

        var socks = relevant.FirstOrDefault(item => IsSocksProxyPort(item.Port));
        if (socks.Port != 0)
        {
            return ("127.0.0.1", socks.Port, true, socks.Pid);
        }

        foreach (var item in relevant
                     .Where(item => item.Port is >= 1080 and < 49152 &&
                                    !IgnorePorts.Contains(item.Port) &&
                                    !IsIgnoredProcess(item.Pid, processes))
                     .Take(6))
        {
            if (LooksLikeHttpProxy(item.Port))
            {
                return ("127.0.0.1", item.Port, false, item.Pid);
            }

            if (LooksLikeSocksProxy(item.Port))
            {
                return ("127.0.0.1", item.Port, true, item.Pid);
            }
        }

        return null;
    }

    internal static List<string> MatchFingerprints(IEnumerable<string> processNames)
    {
        var labels = new List<string>();
        foreach (var name in processNames)
        {
            foreach (var (needle, label) in Fingerprints)
            {
                if (name.Contains(needle, StringComparison.OrdinalIgnoreCase) &&
                    !labels.Contains(label))
                {
                    labels.Add(label);
                    break;
                }
            }
        }

        return labels;
    }

    internal static bool IsProxyProcess(int pid, IReadOnlyList<(int Pid, string Name)> processes)
    {
        var owner = processes.FirstOrDefault(item => item.Pid == pid);
        return !string.IsNullOrWhiteSpace(owner.Name) && IdentifyProcess(owner.Name) is not null;
    }

    private static bool IsIgnoredProcess(int pid, IReadOnlyList<(int Pid, string Name)> processes)
    {
        var owner = processes.FirstOrDefault(item => item.Pid == pid);
        return string.IsNullOrWhiteSpace(owner.Name) || IgnoreProcessNames.Contains(owner.Name);
    }

    internal static bool LooksLikeHttpProxy(int port) =>
        ProbeLoopback(port, Encoding.ASCII.GetBytes("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n"),
            buffer => buffer.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase));

    internal static bool LooksLikeSocksProxy(int port) =>
        ProbeLoopback(port, [0x05, 0x01, 0x00], buffer => buffer.Length >= 2 && buffer[0] == '\x05');

    private static bool ProbeLoopback(int port, byte[] payload, Func<string, bool> accept)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            socket.Blocking = false;
            try
            {
                socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
            {
                // Non-blocking connect.
            }

            if (!socket.Poll(80_000, SelectMode.SelectWrite) || !socket.Connected)
            {
                return false;
            }

            socket.Blocking = true;
            socket.SendTimeout = 80;
            socket.ReceiveTimeout = 80;
            socket.Send(payload);
            var buffer = new byte[24];
            var read = socket.Receive(buffer);
            return read > 0 && accept(Encoding.ASCII.GetString(buffer, 0, read));
        }
        catch
        {
            return false;
        }
    }

    private static List<(int Pid, string Name)> SafeProcesses()
    {
        var list = new List<(int Pid, string Name)>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return list;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(process.ProcessName))
                    {
                        list.Add((process.Id, process.ProcessName));
                    }
                }
                catch
                {
                    // Access denied on some system processes.
                }
            }
        }

        return list;
    }

    private static bool TunnelAdapterUp()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (LooksLikeTunnel(adapter.Name, adapter.Description))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static (bool enabled, string? server, bool pac) ReadSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null)
            {
                return (false, null, false);
            }

            var enabled = key.GetValue("ProxyEnable") is int flag && flag == 1;
            var server = key.GetValue("ProxyServer") as string;
            var pac = !string.IsNullOrWhiteSpace(key.GetValue("AutoConfigURL") as string);
            return (enabled && !string.IsNullOrWhiteSpace(server), server, pac);
        }
        catch
        {
            return (false, null, false);
        }
    }

    internal static string? ReadEnvProxy()
    {
        foreach (var key in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string BuildPathLabel(
        bool tunnel,
        bool proxyOn,
        string? proxyServer,
        bool pac,
        string? envProxy,
        (string Host, int Port, bool Socks, int Pid)? discovered)
    {
        if (tunnel)
        {
            return "TUN / TAP 隧道";
        }

        if (proxyOn)
        {
            return string.IsNullOrWhiteSpace(proxyServer) ? "系统代理" : $"系统代理 {proxyServer}";
        }

        if (pac)
        {
            return "系统 PAC";
        }

        if (!string.IsNullOrWhiteSpace(envProxy))
        {
            return $"环境变量代理 {envProxy}";
        }

        if (discovered is { } proxy)
        {
            var kind = proxy.Socks ? "SOCKS" : "HTTP";
            return $"本地{kind}代理 {proxy.Host}:{proxy.Port}（未设系统代理）";
        }

        return "直连";
    }
}

internal sealed class LiveLocalProxy : IWebProxy
{
    // Domestic services must not be sent through the overseas VPN proxy: the CN gas stations
    // (codebuddy.cn / workbuddy.cn / copilot.tencent.com / minimaxi.com / *.cn) are reachable
    // directly and some of them reject or throttle the tunnel path.
    private static readonly string[] DomesticHosts = ["tencent.com", "minimaxi.com"];

    public ICredentials? Credentials { get; set; }

    internal static bool IsDomesticHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.EndsWith(".cn", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var suffix in DomesticHosts)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public Uri? GetProxy(Uri destination)
    {
        if (IsDomesticHost(destination.Host))
        {
            return null;
        }

        var client = ClientDetector.Peek();
        if (client.TunnelUp)
        {
            return null;
        }

        if (client.SystemProxy)
        {
            try
            {
                return HttpClient.DefaultProxy.GetProxy(destination);
            }
            catch
            {
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(client.DiscoveredProxy))
        {
            var scheme = client.DiscoveredSocks ? "socks5" : "http";
            return new Uri($"{scheme}://{client.DiscoveredProxy}");
        }

        return null;
    }

    public bool IsBypassed(Uri host) => GetProxy(host) is null;
}

