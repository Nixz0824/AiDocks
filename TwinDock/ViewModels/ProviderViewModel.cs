using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using TwinDock.Models;
using TwinDock.Services;

namespace TwinDock.ViewModels;

public sealed class QuotaWindowViewModel(QuotaWindow window)
{
    public string Label { get; } = window.Label;
    public double UsedPercent { get; } = Math.Clamp(window.UsedPercent, 0, 100);
    public double RemainingPercent { get; } = QuotaPresentation.Remaining(window.UsedPercent);
    public Brush BarBrush { get; } = QuotaPresentation.BrushForRemaining(QuotaPresentation.Remaining(window.UsedPercent));
    public string PercentText { get; } = $"{Math.Round(Math.Clamp(window.UsedPercent, 0, 100), MidpointRounding.AwayFromZero):0}% 已用";
    public string ResetText { get; } = FormatReset(window.ResetsAt);

    private static string FormatReset(DateTimeOffset? reset)
    {
        if (reset is null)
        {
            return "重置时间未知";
        }

        var local = reset.Value.ToLocalTime();
        var remaining = local - DateTimeOffset.Now;
        if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(24))
        {
            if (remaining.TotalHours >= 1)
            {
                return $"{(int)remaining.TotalHours} 小时后";
            }

            return $"{Math.Max(1, remaining.Minutes)} 分钟后";
        }

        return $"{local:ddd HH:mm}";
    }
}

public sealed class ProviderViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush Mute = Freeze(142, 142, 147);
    private static readonly SolidColorBrush Healthy = Freeze(32, 227, 162);
    private static readonly SolidColorBrush Fair = Freeze(255, 214, 10);
    private static readonly SolidColorBrush Unstable = Freeze(255, 159, 10);
    private static readonly SolidColorBrush Down = Freeze(255, 69, 58);

    private QuotaSnapshot _snapshot;

    public ProviderViewModel(QuotaSnapshot snapshot)
    {
        _snapshot = snapshot;
        Windows = new ObservableCollection<QuotaWindowViewModel>(snapshot.Windows.Select(window => new QuotaWindowViewModel(window)));
    }

    public string Id => _snapshot.ProviderId;
    public string DisplayName => _snapshot.DisplayName;
    public string Glyph => _snapshot.Glyph;
    public Brush AccentBrush => QuotaPresentation.BrushForRemaining(SummaryPercent);
    public Brush PipBrush => IsReady ? AccentBrush : Mute;
    public double SummaryPercent
    {
        get
        {
            var headline = QuotaPresentation.HeadlineWindow(_snapshot.Windows);
            return headline is null ? 0 : QuotaPresentation.Remaining(headline.UsedPercent);
        }
    }
    public string SummaryText => IsReady ? $"{Math.Round(SummaryPercent, MidpointRounding.AwayFromZero):0}%" : "—";
    public bool IsReady => _snapshot.State is ProviderState.Ready or ProviderState.Stale;
    public bool ShowQuota => IsReady && Windows.Count > 0;
    public bool ShowLogin => !ShowQuota;
    public string LoginTitle => _snapshot.State switch
    {
        ProviderState.Loading => "正在读取额度",
        ProviderState.AuthenticationRequired => "登录已失效",
        ProviderState.Unavailable => "暂时无法读取",
        ProviderState.Stale => "显示上次数据",
        ProviderState.Ready => "没有可用额度",
        _ => "还没有登录"
    };
    public string LoginHint => _snapshot.StatusMessage ?? "登录后会自动同步额度。";
    public string LoginFooter => _snapshot.State is ProviderState.MissingCredentials or ProviderState.AuthenticationRequired
        ? "登录后会自动同步额度，无需手动刷新。"
        : "应用会定时向服务商同步额度。";
    public string StatusText => _snapshot.State switch
    {
        ProviderState.Loading => "正在读取额度…",
        ProviderState.MissingCredentials => _snapshot.StatusMessage ?? "未检测到登录",
        ProviderState.AuthenticationRequired => _snapshot.StatusMessage ?? "登录已失效",
        ProviderState.Unavailable => _snapshot.StatusMessage ?? "暂时无法读取额度",
        ProviderState.Stale => _snapshot.StatusMessage ?? "显示上次有效数据",
        _ => $"更新于 {DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)}"
    };
    public ObservableCollection<QuotaWindowViewModel> Windows { get; }
    public bool ShowLine => ProbeCatalog.Find(Id) is not null;
    public string LineSubtitle { get; private set; } = "正在探测 AI 线路";
    public Brush LineAccentBrush { get; private set; } = Mute;
    public Brush OfficialBrush { get; private set; } = Mute;
    public string DelayValue { get; private set; } = "…";
    public Brush DelayBrush { get; private set; } = Mute;
    public string LossValue { get; private set; } = "丢包 —";
    public Brush LossBrush { get; private set; } = Mute;
    public string JitterValue { get; private set; } = "抖动 —";
    public Brush JitterBrush { get; private set; } = Mute;
    public string MainlandText { get; private set; } = "国内·—";
    public string LocationText { get; private set; } = "";
    public IList<double?> SparkValues { get; private set; } = Array.Empty<double?>();

    public void ApplyLine(ProbeSnapshot snapshot, IReadOnlyList<ProbeSnapshot> history)
    {
        if (!ShowLine)
        {
            return;
        }

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
        LineAccentBrush = BrushFor(status);
        LineSubtitle = BuildLineSubtitle(snapshot, status);
        OfficialBrush = official.Level switch
        {
            OfficialLevel.Ok => Healthy,
            OfficialLevel.Degraded => Unstable,
            OfficialLevel.Down => Down,
            _ => Mute
        };
        DelayValue = latency is int ms ? $"{ms} ms" : "超时";
        DelayBrush = LineAccentBrush;
        LossValue = $"丢包 {loss:0.#}%";
        LossBrush = BrushForLoss(loss);
        JitterValue = $"抖动 {jitter} ms";
        JitterBrush = BrushForJitter(jitter);
        MainlandText = snapshot.MainlandLatencyMs is int mainland
            ? $"国内·{mainland}ms"
            : "国内·—";
        LocationText = LineCopy.LocationName(snapshot.Location);
        SparkValues = history.Append(snapshot)
            .TakeLast(30)
            .Select(item =>
            {
                var point = item.Endpoints.FirstOrDefault(endpoint => endpoint.Id.Equals(Id, StringComparison.OrdinalIgnoreCase));
                return point is { Ok: true } ? (double?)point.LatencyMs : (double?)null;
            })
            .ToArray();
        OnPropertyChanged(string.Empty);
    }

    public void Apply(QuotaSnapshot snapshot)
    {
        _snapshot = snapshot;
        Windows.Clear();
        foreach (var window in snapshot.Windows)
        {
            Windows.Add(new QuotaWindowViewModel(window));
        }

        OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string BuildLineSubtitle(ProbeSnapshot snapshot, LineStatus status)
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
}

public sealed class CatalogItemViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;

    public CatalogItemViewModel(string id, string displayName, bool isEnabled)
    {
        Id = id;
        DisplayName = displayName;
        _isEnabled = isEnabled;
    }

    public string Id { get; }
    public string DisplayName { get; }

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

    public string CheckText => IsEnabled ? "✓" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
