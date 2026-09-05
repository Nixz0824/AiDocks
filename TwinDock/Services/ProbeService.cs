using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using TwinDock.Models;

namespace TwinDock.Services;

internal sealed class ProbeService : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(2800);
    private readonly HttpClient _http;
    private readonly HttpClient _directHttp;
    private readonly OfficialStatusClient _official;
    private int _demoTick;

    public event Action<IReadOnlyDictionary<string, OfficialStatus>>? OfficialReady;

    public ProbeService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = ProbeTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = ProbeTimeout
        };
        _directHttp = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(1800),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = TimeSpan.FromMilliseconds(1800)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TwinDock/0.1");
        _directHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TwinDock/0.1");
        _official = new OfficialStatusClient();
    }

    public async Task<ProbeSnapshot> RunAsync(IReadOnlyList<string> enabledIds, bool demo, CancellationToken cancellationToken)
    {
        if (demo)
        {
            return Demo(enabledIds);
        }

        var client = ClientDetector.Detect();
        var routed = ResolveHttp(client, out var ownsRouted);
        try
        {
            var ai = ProbeCatalog.Selected(enabledIds).ToArray();
            var targets = ai.Append(ProbeCatalog.Mainland).ToArray();
            var tasks = targets.Select(target => ProbeTargetAsync(target, routed, cancellationToken)).ToList();
            var traceTask = ReadTraceAsync(routed, cancellationToken);
            var officialTask = _official.ReadAsync(ai, false, cancellationToken);
            var directTask = client.TunnelUp
                ? Task.FromResult<bool?>(null)
                : ProbeDirectCloudflareAsync(cancellationToken);

            await Task.WhenAll(tasks);
            var endpoints = new List<EndpointResult>(tasks.Count);
            foreach (var task in tasks)
            {
                endpoints.Add(await task);
            }

            var (ip, loc) = await traceTask;
            var direct = await directTask;
            IReadOnlyDictionary<string, OfficialStatus> official;
            if (officialTask.IsCompleted)
            {
                official = await officialTask;
            }
            else
            {
                official = _official.Peek(ai);
                _ = CompleteOfficialAsync(officialTask);
            }

            return new ProbeSnapshot
            {
                At = DateTimeOffset.Now,
                Client = client,
                Endpoints = endpoints,
                PublicIp = ip,
                Location = loc,
                DirectOverseasOk = direct,
                Official = official
            };
        }
        finally
        {
            if (ownsRouted)
            {
                routed.Dispose();
            }
        }
    }

    internal ProbeSnapshot Demo(IReadOnlyList<string> enabledIds)
    {
        var tick = Interlocked.Increment(ref _demoTick);
        var wave = 70 + (int)(42 * Math.Sin(tick / 4.2));
        var fail = tick % 17 == 0;
        var endpoints = ProbeCatalog.Selected(enabledIds)
            .Select((target, index) => new EndpointResult
            {
                Id = target.Id,
                Label = target.Label,
                Overseas = true,
                Ok = !fail || index == 0,
                LatencyMs = fail && index != 0 ? 2800 : wave + index * 18
            })
            .ToList();
        endpoints.Add(new EndpointResult
        {
            Id = ProbeCatalog.Mainland.Id,
            Label = ProbeCatalog.Mainland.Label,
            Overseas = false,
            Ok = true,
            LatencyMs = 22 + tick % 9
        });

        return new ProbeSnapshot
        {
            At = DateTimeOffset.Now,
            Client = new ClientPresence
            {
                TntRunning = true,
                SystemProxy = true,
                ProxyServer = "127.0.0.1:7890",
                DisplayName = "tntcloud Lite",
                PathLabel = "系统代理 127.0.0.1:7890"
            },
            Endpoints = endpoints,
            PublicIp = "203.0.113.42",
            Location = "JP",
            DirectOverseasOk = false,
            Official = ProbeCatalog.Selected(enabledIds).ToDictionary(
                target => target.Id,
                target => new OfficialStatus { TargetId = target.Id, Level = OfficialLevel.Ok, Label = "官方正常" },
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task CompleteOfficialAsync(Task<IReadOnlyDictionary<string, OfficialStatus>> officialTask)
    {
        try
        {
            var official = await officialTask.ConfigureAwait(false);
            OfficialReady?.Invoke(official);
        }
        catch
        {
            // Keep the last cached official status.
        }
    }

    private HttpClient ResolveHttp(ClientPresence client, out bool owns)
    {
        owns = false;
        if (client.TunnelUp || client.SystemProxy || string.IsNullOrWhiteSpace(client.DiscoveredProxy))
        {
            return _http;
        }

        owns = true;
        var scheme = client.DiscoveredSocks ? "socks5" : "http";
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"{scheme}://{client.DiscoveredProxy}"),
            UseProxy = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = ProbeTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var http = new HttpClient(handler) { Timeout = ProbeTimeout };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TwinDock/0.1");
        return http;
    }

    private async Task<EndpointResult> ProbeTargetAsync(ProbeTarget target, HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(target.HttpUrl))
            {
                var result = await HttpAsync(http, target.HttpUrl, cancellationToken);
                return new EndpointResult
                {
                    Id = target.Id,
                    Label = target.Label,
                    Overseas = target.Overseas,
                    Ok = result.ok,
                    LatencyMs = result.ms
                };
            }

            var tcp = await TcpAsync(target.Host, target.Port, ProbeTimeout, cancellationToken);
            return new EndpointResult
            {
                Id = target.Id,
                Label = target.Label,
                Overseas = target.Overseas,
                Ok = tcp.ok,
                LatencyMs = tcp.ms
            };
        }
        catch
        {
            return new EndpointResult
            {
                Id = target.Id,
                Label = target.Label,
                Overseas = target.Overseas,
                Ok = false,
                LatencyMs = (int)ProbeTimeout.TotalMilliseconds
            };
        }
    }

    private async Task<(string? ip, string? loc)> ReadTraceAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return LineScoring.ParseCloudflareTrace(body);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<bool?> ProbeDirectCloudflareAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tcp = await TcpAsync("1.1.1.1", 443, TimeSpan.FromMilliseconds(1800), cancellationToken);
            if (tcp.ok)
            {
                return true;
            }

            var http = await HttpAsync(_directHttp, "https://1.1.1.1/", cancellationToken);
            return http.ok;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool ok, int ms)> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            await client.ConnectAsync(host, port, linked.Token);
            return (client.Connected, Math.Max(1, (int)clock.ElapsedMilliseconds));
        }
        catch
        {
            return (false, Math.Max(1, (int)clock.ElapsedMilliseconds));
        }
    }

    private static async Task<(bool ok, int ms)> HttpAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var ok = (int)response.StatusCode is >= 200 and < 500;
            return (ok, Math.Max(1, (int)clock.ElapsedMilliseconds));
        }
        catch
        {
            return (false, Math.Max(1, (int)clock.ElapsedMilliseconds));
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _directHttp.Dispose();
        _official.Dispose();
    }
}
