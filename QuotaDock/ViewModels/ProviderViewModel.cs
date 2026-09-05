using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using QuotaDock.Models;

namespace QuotaDock.ViewModels;

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
