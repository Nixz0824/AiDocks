using System.Windows.Media;
using TwinDock.Models;
using Color = System.Windows.Media.Color;

namespace TwinDock.Services;

internal static class LineScoring
{
    public const int GreenMaxMs = 160;
    public const int YellowMaxMs = 280;
    public const int OrangeMaxMs = 480;

    public static ProbeSnapshot Classify(ProbeSnapshot draft, IReadOnlyList<ProbeSnapshot> history)
    {
        var overseas = draft.Endpoints.Where(item => item.Overseas).ToArray();
        var mainland = draft.Endpoints.FirstOrDefault(item => !item.Overseas);
        var overseasOk = overseas.Any(item => item.Ok);
        var mainlandOk = mainland?.Ok == true;
        var latencies = overseas.Where(item => item.Ok).Select(item => item.LatencyMs).ToArray();
        var currentLatency = Median(latencies);
        var window = history.Append(draft).TakeLast(18).ToArray();
        var overseasAttempts = window.SelectMany(item => item.Endpoints.Where(endpoint => endpoint.Overseas)).ToArray();
        var loss = overseasAttempts.Length == 0
            ? (overseasOk ? 0 : 100)
            : 100d * overseasAttempts.Count(item => !item.Ok) / overseasAttempts.Length;
        var recentLatencies = window
            .SelectMany(item => item.Endpoints.Where(endpoint => endpoint.Overseas && endpoint.Ok))
            .Select(item => item.LatencyMs)
            .ToArray();
        var jitter = Jitter(recentLatencies);
        var health = Health(loss, currentLatency ?? (recentLatencies.Length == 0 ? null : Median(recentLatencies)), jitter);
        var clientOrPath = draft.Client.AnyClient || draft.Client.TunnelUp || draft.Client.SystemProxy;
        var status = Connect(mainlandOk, overseasOk, clientOrPath, currentLatency);
        var pip = Worst(overseas, mainlandOk, clientOrPath);

        return new ProbeSnapshot
        {
            At = draft.At,
            Client = draft.Client,
            Endpoints = draft.Endpoints,
            PublicIp = draft.PublicIp,
            Location = draft.Location,
            DirectOverseasOk = draft.DirectOverseasOk,
            MainlandOk = mainlandOk,
            OverseasOk = overseasOk,
            OverseasLatencyMs = currentLatency,
            MainlandLatencyMs = mainland is { Ok: true } ? mainland.LatencyMs : null,
            LossPercent = Math.Round(loss, 1),
            JitterMs = jitter,
            Health = health,
            Status = status,
            PipStatus = pip.Status,
            PipLatencyMs = pip.LatencyMs,
            PipSourceId = pip.Id,
            PipSourceLabel = pip.Label,
            Official = draft.Official
        };
    }

    public static LineStatus Connect(bool mainlandOk, bool ok, bool clientOrPath, int? latencyMs)
    {
        if (!mainlandOk && !ok)
        {
            return LineStatus.Offline;
        }

        if (!ok)
        {
            return clientOrPath ? LineStatus.ClientIdle : LineStatus.DirectBlocked;
        }

        return FromLatency(latencyMs);
    }

    public static LineStatus FromLatency(int? latencyMs)
    {
        if (latencyMs is not int ms)
        {
            return LineStatus.Poor;
        }

        if (ms <= GreenMaxMs)
        {
            return LineStatus.Healthy;
        }

        if (ms <= YellowMaxMs)
        {
            return LineStatus.Fair;
        }

        if (ms <= OrangeMaxMs)
        {
            return LineStatus.Unstable;
        }

        return LineStatus.Poor;
    }

    public static (LineStatus Status, int? LatencyMs, string Id, string Label) Worst(
        IReadOnlyList<EndpointResult> overseas,
        bool mainlandOk,
        bool clientOrPath)
    {
        if (overseas.Count == 0)
        {
            return (LineStatus.Probing, null, "", "");
        }

        var failed = overseas.Where(item => !item.Ok).ToArray();
        if (failed.Length > 0)
        {
            var first = failed[0];
            return (Connect(mainlandOk, false, clientOrPath, null), null, first.Id, first.Label);
        }

        var worst = overseas.OrderByDescending(item => item.LatencyMs).First();
        return (FromLatency(worst.LatencyMs), worst.LatencyMs, worst.Id, worst.Label);
    }

    public static Color ColorFor(LineStatus status) => status switch
    {
        LineStatus.Probing => Color.FromRgb(242, 242, 243),
        LineStatus.Healthy => Color.FromRgb(32, 227, 162),
        LineStatus.Fair => Color.FromRgb(255, 214, 10),
        LineStatus.Unstable => Color.FromRgb(255, 159, 10),
        _ => Color.FromRgb(255, 69, 58)
    };

    public static Color PipColor(LineStatus status, int? latencyMs) =>
        ColorFor(status is LineStatus.Probing or LineStatus.Offline or LineStatus.DirectBlocked or LineStatus.ClientIdle
            ? status
            : FromLatency(latencyMs));

    public static double Health(double lossPercent, int? latencyMs, int jitterMs)
    {
        var success = Math.Clamp(100 - lossPercent, 0, 100);
        var latencyScore = latencyMs is int ms
            ? Math.Clamp(100 - Math.Max(0, ms - 80) * (100d / 720d), 0, 100)
            : 0;
        var jitterScore = Math.Clamp(100 - Math.Max(0, jitterMs - 10) * (100d / 140d), 0, 100);
        return Math.Round(success * 0.70 + latencyScore * 0.20 + jitterScore * 0.10, 1);
    }

    public static double RingPercent(LineStatus status, int? latencyMs)
    {
        if (status is LineStatus.Offline or LineStatus.DirectBlocked or LineStatus.ClientIdle)
        {
            return 8;
        }

        if (latencyMs is not int ms)
        {
            return 8;
        }

        return Math.Clamp(100 - Math.Max(0, ms - 80) * (100d / 400d), 12, 100);
    }

    public static int? Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (int)Math.Round((sorted[mid - 1] + sorted[mid]) / 2d)
            : sorted[mid];
    }

    public static int Jitter(IReadOnlyList<int> latencies)
    {
        if (latencies.Count < 2)
        {
            return 0;
        }

        var mean = latencies.Average();
        var variance = latencies.Sum(value => (value - mean) * (value - mean)) / latencies.Count;
        return (int)Math.Round(Math.Sqrt(variance));
    }

    public static (string? ip, string? loc) ParseCloudflareTrace(string body)
    {
        string? ip = null;
        string? loc = null;
        foreach (var line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0].Equals("ip", StringComparison.OrdinalIgnoreCase))
            {
                ip = parts[1].Trim();
            }
            else if (parts[0].Equals("loc", StringComparison.OrdinalIgnoreCase))
            {
                loc = parts[1].Trim();
            }
        }

        return (ip, loc);
    }
}
