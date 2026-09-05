using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LineDock.Models;
using LineDock.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LineDock.ViewModels;

public sealed class MetricRow
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Side { get; init; } = "";
    public double Percent { get; init; }
    public Brush BarBrush { get; init; } = Brushes.White;
}

public sealed class EndpointRow
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "—";
    public Brush DotBrush { get; init; } = Brushes.Gray;
}

public sealed class CatalogItemViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;

    public CatalogItemViewModel(string id, string displayName, bool enabled)
    {
        Id = id;
        DisplayName = displayName;
        _isEnabled = enabled;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string CheckText => _isEnabled ? "✓" : "";
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CheckText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class LineViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush Healthy = Freeze(32, 227, 162);
    private static readonly SolidColorBrush Fair = Freeze(255, 214, 10);
    private static readonly SolidColorBrush Unstable = Freeze(255, 159, 10);
    private static readonly SolidColorBrush Down = Freeze(255, 69, 58);
    private static readonly SolidColorBrush Mute = Freeze(142, 142, 147);

    public LineViewModel(ProbeTarget target)
    {
        Target = target;
        Id = target.Id;
        DisplayName = target.Label;
        StatusTitle = $"{target.Label}探测中";
        SummaryText = "…";
        AccentBrush = Mute;
        IsReady = false;
        Health = 0;
        SparkValues = Array.Empty<double?>();
        Metrics = [];
        Endpoints = [];
        Subtitle = "正在探测 AI 线路";
        Footer = "";
        OfficialText = "官方未知";
        OfficialBrush = Mute;
        VerdictText = "";
        DelayValue = "…";
        DelayBrush = Mute;
        LossValue = "丢包 —";
        LossBrush = Mute;
        JitterValue = "抖动 —";
        JitterBrush = Mute;
        MainlandText = "";
        LocationText = "";
        HasVerdict = false;
        ShowSpark = true;
        DetailHeight = 220;
    }

    public ProbeTarget Target { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string StatusTitle { get; private set; }
    public string Subtitle { get; private set; }
    public string Footer { get; private set; }
    public string OfficialText { get; private set; }
    public Brush OfficialBrush { get; private set; }
    public string VerdictText { get; private set; }
    public bool HasVerdict { get; private set; }
    public string DelayValue { get; private set; }
    public Brush DelayBrush { get; private set; }
    public string LossValue { get; private set; }
    public Brush LossBrush { get; private set; }
    public string JitterValue { get; private set; }
    public Brush JitterBrush { get; private set; }
    public string MainlandText { get; private set; }
    public string LocationText { get; private set; }
    public string SummaryText { get; private set; }
    public double Health { get; private set; }
    public bool IsReady { get; private set; }
    public bool ShowSpark { get; }
    public double DetailHeight { get; }
    public Brush AccentBrush { get; private set; }
    public IList<double?> SparkValues { get; private set; }
    public ObservableCollection<MetricRow> Metrics { get; }
    public ObservableCollection<EndpointRow> Endpoints { get; }

    public void Apply(ProbeSnapshot snapshot, IReadOnlyList<ProbeSnapshot> history)
    {
        var mine = snapshot.Endpoints.FirstOrDefault(item => item.Id.Equals(Id, StringComparison.OrdinalIgnoreCase));
        var ok = mine?.Ok == true;
        var latency = ok ? mine!.LatencyMs : (int?)null;
        var window = history.Append(snapshot).TakeLast(18).ToArray();
        var attempts = window
            .SelectMany(item => item.Endpoints.Where(endpoint => endpoint.Id.Equals(Id, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var loss = attempts.Length == 0
            ? (ok ? 0 : 100)
            : 100d * attempts.Count(item => !item.Ok) / attempts.Length;
        var recent = attempts.Where(item => item.Ok).Select(item => item.LatencyMs).ToArray();
        var jitter = LineScoring.Jitter(recent);
        var status = LineScoring.Connect(
            snapshot.MainlandOk,
            ok,
            snapshot.Client.AnyClient || snapshot.Client.TunnelUp || snapshot.Client.SystemProxy,
            latency);
        snapshot.Official.TryGetValue(Id, out var official);
        official ??= new OfficialStatus { TargetId = Id, Level = OfficialLevel.Unknown, Label = "官方未知" };

        StatusTitle = LineCopy.StatusTitle(status, DisplayName);
        SummaryText = LineCopy.Summary(status, latency);
        Health = LineScoring.RingPercent(status, latency);
        IsReady = status is not LineStatus.Probing;
        AccentBrush = BrushFor(status);
        Subtitle = BuildSubtitle(snapshot, status);
        OfficialText = official.Label;
        OfficialBrush = official.Level switch
        {
            OfficialLevel.Ok => Healthy,
            OfficialLevel.Degraded => Unstable,
            OfficialLevel.Down => Down,
            _ => Mute
        };
        VerdictText = BuildVerdict(status, official);
        HasVerdict = !string.IsNullOrWhiteSpace(VerdictText);
        DelayValue = latency is int ms ? $"{ms} ms" : "超时";
        DelayBrush = AccentBrush;
        LossValue = $"丢包 {loss:0.#}%";
        LossBrush = BrushForLoss(loss);
        JitterValue = $"抖动 {jitter} ms";
        JitterBrush = BrushForJitter(jitter);
        MainlandText = snapshot.MainlandLatencyMs is int mainland
            ? $"国内·{mainland}ms"
            : "国内·—";
        LocationText = LineCopy.LocationName(snapshot.Location);
        Footer = RelativeTime(snapshot.At);
        SparkValues = history.Append(snapshot)
            .TakeLast(30)
            .Select(item =>
            {
                var point = item.Endpoints.FirstOrDefault(endpoint => endpoint.Id.Equals(Id, StringComparison.OrdinalIgnoreCase));
                return point is { Ok: true } ? (double?)point.LatencyMs : (double?)null;
            })
            .ToArray();

        RaiseAll();
    }

    private static string BuildSubtitle(ProbeSnapshot snapshot, LineStatus status)
    {
        var name = snapshot.Client.DisplayName;
        if (status is LineStatus.DirectBlocked)
        {
            return "未开代理";
        }

        if (status is LineStatus.Offline)
        {
            return "网络离线";
        }

        if (status is LineStatus.ClientIdle)
        {
            return $"{name} · 未生效";
        }

        if (snapshot.Client.AnyClient)
        {
            return $"{name} · 已生效";
        }

        return name;
    }

    private static string BuildVerdict(LineStatus status, OfficialStatus official)
    {
        var mineDown = status is LineStatus.DirectBlocked or LineStatus.ClientIdle or LineStatus.Offline;
        var mineSlow = status is LineStatus.Unstable or LineStatus.Poor;
        if (official.Level is OfficialLevel.Down)
        {
            return mineDown ? "官方故障，不是 VPN" : "官方故障，你这边还能通";
        }

        if (official.Level is OfficialLevel.Degraded)
        {
            return mineDown || mineSlow ? "官方降级，先看官方" : "官方降级，你这边还正常";
        }

        if (official.Level is OfficialLevel.Unknown)
        {
            return "";
        }

        if (mineDown)
        {
            return "官方正常，问题在线路";
        }

        if (mineSlow)
        {
            return "官方正常，偏慢是线路";
        }

        return "";
    }

    private static string RelativeTime(DateTimeOffset at)
    {
        var delta = DateTimeOffset.Now - at;
        if (delta.TotalSeconds < 8)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalSeconds)} 秒前";
        }

        return $"{Math.Max(1, (int)delta.TotalMinutes)} 分钟前";
    }

    private static Brush BrushForLoss(double percent) =>
        percent <= 0.5 ? Healthy : percent <= 8 ? Fair : percent <= 20 ? Unstable : Down;

    private static Brush BrushForJitter(int ms) =>
        ms <= 20 ? Healthy : ms <= 60 ? Fair : ms <= 120 ? Unstable : Down;

    private static Brush BrushFor(LineStatus status)
    {
        var color = LineScoring.ColorFor(status);
        return Freeze(color.R, color.G, color.B);
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Footer));
        OnPropertyChanged(nameof(OfficialText));
        OnPropertyChanged(nameof(OfficialBrush));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(HasVerdict));
        OnPropertyChanged(nameof(DelayValue));
        OnPropertyChanged(nameof(DelayBrush));
        OnPropertyChanged(nameof(LossValue));
        OnPropertyChanged(nameof(LossBrush));
        OnPropertyChanged(nameof(JitterValue));
        OnPropertyChanged(nameof(JitterBrush));
        OnPropertyChanged(nameof(MainlandText));
        OnPropertyChanged(nameof(LocationText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(SparkValues));
        OnPropertyChanged(nameof(Metrics));
        OnPropertyChanged(nameof(Endpoints));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
