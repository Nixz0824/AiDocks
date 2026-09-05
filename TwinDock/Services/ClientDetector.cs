using System.Diagnostics;
using System.Net.NetworkInformation;
using TwinDock.Models;
using TwinDock.Native;
using Microsoft.Win32;

namespace TwinDock.Services;

internal static class ClientDetector
{
    private static readonly (string Needle, string Label)[] Fingerprints =
    [
        ("tntcloudLiteCore", "tntcloud Lite"),
        ("tntcloudLite", "tntcloud Lite"),
        ("tntcloud", "tntcloud Lite"),
        ("YunyunCore", "云云"),
        ("Yunyun", "云云"),
        ("Clash Verge", "Clash Verge"),
        ("Clash-Verge", "Clash Verge"),
        ("clash-verge", "Clash Verge"),
        ("ClashVerge", "Clash Verge"),
        ("Clash for Windows", "Clash"),
        ("ClashN", "ClashN"),
        ("clash-meta", "Clash Meta"),
        ("ClashMeta", "Clash Meta"),
        ("mihomo", "Mihomo"),
        ("clash", "Clash"),
        ("v2rayN", "v2rayN"),
        ("v2rayn", "v2rayN"),
        ("v2rayW", "V2RayW"),
        ("v2ray", "V2Ray"),
        ("xray", "Xray"),
        ("sing-box", "sing-box"),
        ("singbox", "sing-box"),
        ("NekoRay", "NekoRay"),
        ("nekoray", "NekoRay"),
        ("NekoBox", "NekoBox"),
        ("hiddify", "Hiddify"),
        ("hysteria", "Hysteria"),
        ("ss-local", "Shadowsocks"),
        ("shadowsocks", "Shadowsocks"),
        ("outline", "Outline"),
        ("wireguard", "WireGuard"),
        ("tailscale", "Tailscale"),
        ("CloudflareWARP", "Cloudflare WARP"),
        ("warp-svc", "Cloudflare WARP"),
        ("openvpn", "OpenVPN"),
        ("proxifier", "Proxifier"),
        ("sstap", "SSTAP"),
        ("naiveproxy", "NaiveProxy"),
        ("trojan", "Trojan"),
        ("Qv2ray", "Qv2ray"),
        ("qv2ray", "Qv2ray"),
        ("astrill", "Astrill"),
        ("ExpressVPN", "ExpressVPN"),
        ("NordVPN", "NordVPN"),
        ("surfshark", "Surfshark"),
        ("mullvad", "Mullvad"),
        ("ProtonVPN", "Proton VPN"),
        ("Windscribe", "Windscribe"),
        ("SoftEther", "SoftEther"),
        ("ZeroTier", "ZeroTier"),
        ("tun2socks", "tun2socks")
    ];

    private static readonly int[] HttpPorts =
        [7890, 7891, 7897, 10809, 20171, 6152, 8118, 8888, 8080, 2080, 12334, 12335, 2801, 10080, 18080, 7892];

    private static readonly int[] SocksPorts =
        [1080, 10808, 1081, 1086, 1087, 20170, 6153, 10808, 7890];

    private static readonly string[] TunnelHints =
    [
        "tap", "tun", "wintun", "wireguard", "clash", "meta", "sing-box", "singbox",
        "verykuai", "cloudflare warp", "warp", "tailscale", "zerotier", "sstap",
        "outline", "tun2socks", "wintun", "meta tun"
    ];

    public static ClientPresence Detect()
    {
        var processes = SafeProcesses();
        var tunnel = TunnelAdapterUp();
        var (proxyOn, proxyServer, pac) = ReadSystemProxy();
        var listeners = TcpOwners.LoopbackListeners();
        var discovered = PickLocalProxy(listeners, processes);
        var display = ActiveClientName(processes, listeners, discovered, proxyOn, proxyServer, tunnel, pac);
        var tnt = display.Contains("tntcloud", StringComparison.OrdinalIgnoreCase);
        var yunyun = display.Contains("云云", StringComparison.Ordinal);

        var path = BuildPathLabel(tunnel, proxyOn, proxyServer, pac, discovered);
        return new ClientPresence
        {
            TntRunning = tnt,
            YunyunRunning = yunyun,
            TunnelUp = tunnel,
            SystemProxy = proxyOn || pac,
            HasKnownClient = display is not "未检测到代理" and not "系统代理" and not "本地代理" and not "隧道网卡",
            ProxyServer = proxyServer,
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
        bool pac)
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

        if (tunnel)
        {
            return "隧道网卡";
        }

        if (proxyOn || pac)
        {
            return "系统代理";
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
            .Where(item => IsHttpProxyPort(item.Port) || IsSocksProxyPort(item.Port))
            .DistinctBy(item => item.Port)
            .ToArray();
        if (relevant.Length == 0)
        {
            return null;
        }

        var http = relevant.FirstOrDefault(item => IsHttpProxyPort(item.Port) && item.Port != 0);
        if (http.Port != 0)
        {
            return ("127.0.0.1", http.Port, false, http.Pid);
        }

        var socks = relevant[0];
        return ("127.0.0.1", socks.Port, true, socks.Pid);
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

    private static string BuildPathLabel(
        bool tunnel,
        bool proxyOn,
        string? proxyServer,
        bool pac,
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

        if (discovered is { } proxy)
        {
            var kind = proxy.Socks ? "SOCKS" : "HTTP";
            return $"本地{kind}代理 {proxy.Host}:{proxy.Port}（未设系统代理）";
        }

        return "直连";
    }
}
