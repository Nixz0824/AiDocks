using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TwinDock.Models;
using TwinDock.Native;
using TwinDock.Providers;
using TwinDock.Services;
using TwinDock.ViewModels;
using FormsScreen = System.Windows.Forms.Screen;
using DrawingPoint = System.Drawing.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace TwinDock;

public partial class MainWindow : Window
{
    private static readonly KeySpline StrongEaseOut = new(0.23, 1, 0.32, 1);
    private static readonly TimeSpan DetailLeaveDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RailLeaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ClickDetailHold = TimeSpan.FromMilliseconds(480);
    private static readonly TimeSpan PostSnapCollapseDelay = TimeSpan.FromMilliseconds(180);
    private readonly AppSettings _settings;
    private readonly QuotaService _quotaService = new();
    private readonly ProbeService _probeService = new();
    private readonly HistoryStore _lineHistory = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _lineTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _railCollapseTimer;
    private readonly DispatcherTimer _recoveryRefreshTimer;
    private readonly SpringWindowAnimator _snapAnimator;
    private readonly OutsideClickWatcher _outsideClick = new();
    private readonly CredentialChangeWatcher _credentialWatcher = new();
    private readonly string? _initialProviderId;
    private readonly bool _motionPreview;
    private readonly bool _openMenuOnLoad;
    private readonly string? _dragReproPath;
    private readonly string? _pickerReproPath;
    private readonly List<DragSample> _dragSamples = [];
    private CancellationTokenSource? _refreshCancellation;
    private int _refreshGeneration;
    private bool _closed;
    private string? _railPathData;
    private HwndSource? _windowSource;
    private ProviderViewModel? _selectedProvider;
    private ProviderViewModel? _pressedProvider;
    private readonly HashSet<string> _removingIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action> _chromeCompletions = new(StringComparer.OrdinalIgnoreCase);
    private FrameworkElement? _exitingContainer;
    private ProviderViewModel? _exitingViewModel;
    private double _exitFromH;
    private bool _chromeTweenActive;
    private double _chromeFromH;
    private double _chromeToH;
    private double _chromeFromT;
    private double _chromeToT;
    private double _chromeFromC;
    private double _chromeToC;
    private long _chromeStart;
    private int _chromeMs;
    private double _countFrom;
    private double _countTo;
    private double _countT = 1;
    private FrameworkElement? _pressedSurface;
    private bool _cardPinned;
    private bool _pickerOpen;
    private bool _isDragging;
    private bool _dragMoved;
    private bool _floatingChrome;
    private DockEdge? _snapTarget;
    private DockEdge _facing = DockEdge.Right;
    private double _shapeMorph;
    private double _shapeMorphFrom;
    private double _shapeMorphTo;
    private long _shapeMorphStart;
    private bool _demoMode;
    private bool _railExpanded;
    private int _cardTransitionVersion;
    private DateTimeOffset _lastRefreshAt;
    private DateTimeOffset _buttonActivationBlockedUntil;
    private DateTimeOffset _detailHoldUntil;
    private System.Windows.Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double _topBeforePicker;
    private double _detailNeed;
    private ProbeSnapshot? _lastDraft;
    private Rect _lastWorkArea;

    public MainWindow(AppSettings settings, bool demoMode, string? initialProviderId = null, bool motionPreview = false, bool openMenuOnLoad = false, string? dragReproPath = null, string? pickerReproPath = null)
    {
        _settings = settings;
        _demoMode = demoMode;
        _initialProviderId = initialProviderId;
        _motionPreview = motionPreview;
        _openMenuOnLoad = openMenuOnLoad;
        _dragReproPath = dragReproPath;
        _pickerReproPath = pickerReproPath;
        InitializeComponent();
        DataContext = this;

        Providers = new ObservableCollection<ProviderViewModel>(
            _quotaService.LoadingSnapshots(_settings.EnabledProviderIds).Select(snapshot => new ProviderViewModel(snapshot)));
        CatalogItems = new ObservableCollection<CatalogItemViewModel>(
            ProviderCatalog.All.Select(item => new CatalogItemViewModel(
                item.Id,
                item.DisplayName.Replace(" Usage", "", StringComparison.Ordinal),
                _settings.EnabledProviderIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))));
        _outsideClick.ButtonDown += OnOutsideMouseDown;
        _credentialWatcher.Changed += OnCredentialChanged;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_settings.RefreshMinutes) };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _lineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _lineTimer.Tick += (_, _) => _ = RefreshLineAsync();
        _probeService.OfficialReady += official => Dispatcher.BeginInvoke(() => ApplyOfficial(official));
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_cardPinned && !_pickerOpen && !_isDragging && !IsMouseOver)
            {
                HideDetail();
            }
        };
        _railCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(620) };
        _railCollapseTimer.Tick += (_, _) =>
        {
            _railCollapseTimer.Stop();
            if (!_cardPinned && !_pickerOpen && !_isDragging && !IsMouseOver)
            {
                CollapseRail();
            }
        };
        _recoveryRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _recoveryRefreshTimer.Tick += (_, _) =>
        {
            _recoveryRefreshTimer.Stop();
            _ = RefreshAsync();
        };

        _snapAnimator = new SpringWindowAnimator(this);
        _snapAnimator.Completed += (_, _) =>
        {
            _snapTarget = null;
            if (_floatingChrome)
            {
                RestoreDockedChrome(_settings.Edge);
                SetShapeMorph(0, immediate: true);
                NativeMethods.RefreshLayeredWindow(this);
            }

            PersistPosition();
            RelayoutRail(force: false);
            if (!_pickerOpen && !IsMouseOver)
            {
                _railCollapseTimer.Stop();
                _railCollapseTimer.Interval = PostSnapCollapseDelay;
                _railCollapseTimer.Start();
            }
        };
    }

    public ObservableCollection<ProviderViewModel> Providers { get; }

    public ObservableCollection<CatalogItemViewModel> CatalogItems { get; }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        NativeMethods.ConfigureUtilityWindow(this);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged += OnSystemDisplayChanged;
        SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _credentialWatcher.Start();
        RelayoutRail();
        PlaceAtSavedPosition();
        CollapseRail(true);
        _refreshTimer.Start();
        _lineTimer.Start();
        _ = RefreshLineAsync();
        try
        {
            await RefreshAsync();
        }
        catch
        {
            // First paint must not depend on a successful quota fetch.
        }
        if (!string.IsNullOrWhiteSpace(_initialProviderId))
        {
            var provider = Providers.FirstOrDefault(item => item.Id.Equals(_initialProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider is not null)
            {
                _hideTimer.Stop();
                _railCollapseTimer.Stop();
                _cardPinned = true;
                ExpandRail();
                SelectProvider(provider);
                await Dispatcher.InvokeAsync(() => ShowDetail(), DispatcherPriority.ContextIdle);
            }
        }
        else if (_motionPreview)
        {
            await RunMotionPreviewAsync();
        }

        if (_openMenuOnLoad)
        {
            ExpandRail(true);
            OpenPicker();
        }

        if (!string.IsNullOrWhiteSpace(_dragReproPath))
        {
            await RunDragReproAsync(_dragReproPath);
        }
        else if (!string.IsNullOrWhiteSpace(_pickerReproPath))
        {
            await RunPickerReproAsync(_pickerReproPath);
        }
    }

    private async Task RunPickerReproAsync(string reportPath)
    {
        await Task.Delay(700);
        var passed = _pickerOpen && PickerSurface.Visibility == Visibility.Visible && PickerSurface.Opacity > 0.95;
        var report = new
        {
            passed,
            pickerOpen = _pickerOpen,
            visibility = PickerSurface.Visibility.ToString(),
            opacity = PickerSurface.Opacity,
            pickerWidth = PickerSurface.ActualWidth,
            pickerHeight = PickerSurface.ActualHeight,
            windowWidth = Width,
            windowHeight = Height,
            canvasLeft = Canvas.GetLeft(PickerSurface),
            canvasTop = Canvas.GetTop(PickerSurface)
        };
        var directory = System.IO.Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        System.Windows.Application.Current.Shutdown(passed ? 0 : 1);
    }

    private async Task RunDragReproAsync(string reportPath)
    {
        ExpandRail(true);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        var screen = FormsScreen.PrimaryScreen ?? FormsScreen.AllScreens[0];
        var work = ScreenWorkingAreaInDip(screen);
        _lastWorkArea = work;
        _settings.Edge = DockEdge.Right;
        RestoreDockedChrome(DockEdge.Right);
        Left = work.Right - Width;
        Top = work.Top + Math.Max(0, (work.Height - Height) / 2);

        var widths = new List<double> { Width };
        var movingShapeChanges = 0;
        var maxRightOvershoot = 0d;
        var lastLeft = Left;
        var lastMorph = _shapeMorph;
        EnsureFloatingChrome();
        widths.Add(Width);
        Left = work.Left + work.Width * 0.42;
        UpdateFloatingShape();
        lastLeft = Left;
        lastMorph = _shapeMorph;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler frame = (_, _) =>
        {
            widths.Add(Width);
            var moved = Math.Abs(Left - lastLeft) > 0.01;
            var morphed = Math.Abs(_shapeMorph - lastMorph) > 0.001;
            if (moved && morphed)
            {
                movingShapeChanges++;
            }

            maxRightOvershoot = Math.Max(maxRightOvershoot, Left + Width - work.Right);
            lastLeft = Left;
            lastMorph = _shapeMorph;
        };
        EventHandler completed = (_, _) => completion.TrySetResult();
        _snapAnimator.Frame += frame;
        _snapAnimator.Completed += completed;
        SnapToEdge(DockEdge.Right, work, 1600, 0);
        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5))) == completion.Task;
        _snapAnimator.Frame -= frame;
        _snapAnimator.Completed -= completed;
        widths.Add(Width);

        await Task.Delay(900);
        var lateWidthA = Width;
        var lateMorphA = _shapeMorph;
        var lateScaleXA = RailScale.ScaleX;
        var lateScaleYA = RailScale.ScaleY;
        await Task.Delay(250);
        var lateWidthB = Width;
        var lateMorphB = _shapeMorph;
        var lateScaleXB = RailScale.ScaleX;
        var lateScaleYB = RailScale.ScaleY;
        var lateStable = Math.Abs(lateWidthA - lateWidthB) < 0.001 &&
                         Math.Abs(lateMorphA - lateMorphB) < 0.001 &&
                         Math.Abs(lateScaleXA - lateScaleXB) < 0.001 &&
                         Math.Abs(lateScaleYA - lateScaleYB) < 0.001;

        var distinctWidths = widths.Select(value => Math.Round(value, 2)).Distinct().Count();
        var failed = !finished || distinctWidths > 1 || movingShapeChanges > 0 || maxRightOvershoot > 0.75 || !lateStable;
        var report = new
        {
            passed = !failed,
            finished,
            distinctWidths,
            widths = widths.Distinct().ToArray(),
            movingShapeChanges,
            maxRightOvershoot,
            lateStable,
            lateWidth = lateWidthB,
            lateMorph = lateMorphB,
            lateScaleX = lateScaleXB,
            lateScaleY = lateScaleYB,
            finalLeft = Left,
            finalTop = Top,
            finalWidth = Width,
            finalMorph = _shapeMorph
        };
        var directory = System.IO.Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        System.Windows.Application.Current.Shutdown(failed ? 1 : 0);
    }

    private async Task RunMotionPreviewAsync()
    {
        await Task.Delay(420);
        ExpandRail();
        await Task.Delay(240);
        if (Providers.Count > 0)
        {
            SelectProvider(Providers[0]);
        }

        await Task.Delay(720);
        if (Providers.Count > 1)
        {
            SelectProvider(Providers[1]);
        }

        await Task.Delay(720);
        _cardPinned = false;
        HideDetail();
        await Task.Delay(360);
        CollapseRail();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _refreshGeneration++;
        SystemEvents.DisplaySettingsChanged -= OnSystemDisplayChanged;
        SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _refreshTimer.Stop();
        _lineTimer.Stop();
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        _recoveryRefreshTimer.Stop();
        _snapAnimator.Stop();
        CancelChromeTween();
        System.Windows.Media.CompositionTarget.Rendering -= OnShapeMorphFrame;
        _refreshCancellation?.Cancel();
        _quotaService.Dispose();
        _probeService.Dispose();
        _refreshCancellation?.Dispose();
        _outsideClick.Dispose();
        _credentialWatcher.Dispose();
        try
        {
            _windowSource?.RemoveHook(WindowMessageHook);
        }
        catch
        {
            // Source may already be disposed.
        }
    }

    private void Window_DpiChanged(object sender, System.Windows.DpiChangedEventArgs e) => RepositionAfterEnvironmentChange();

    private void OnSystemDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RepositionAfterEnvironmentChange);

    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            ScheduleRecoveryRefresh();
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            ScheduleRecoveryRefresh();
        }
    }

    private void OnCredentialChanged(object? sender, EventArgs e) => ScheduleRecoveryRefresh();

    private void ScheduleRecoveryRefresh()
    {
        if (_closed)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
            {
                return;
            }

            _recoveryRefreshTimer.Stop();
            _recoveryRefreshTimer.Start();
        });
    }

    private async Task RefreshAsync()
    {
        if (_closed)
        {
            return;
        }

        var generation = ++_refreshGeneration;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var token = _refreshCancellation.Token;

        try
        {
            await _quotaService.RefreshAsync(_demoMode, _settings.EnabledProviderIds, token, (snapshot, _) =>
            {
                if (_closed || generation != _refreshGeneration || token.IsCancellationRequested)
                {
                    return Task.CompletedTask;
                }

                ApplySnapshot(snapshot);
                return Task.CompletedTask;
            });
            if (_closed || generation != _refreshGeneration || token.IsCancellationRequested)
            {
                return;
            }

            _lastRefreshAt = DateTimeOffset.Now;
            await RefreshLineAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer refresh replaced this one.
        }
        catch (Exception)
        {
            // Keep the last successful snapshots on screen.
        }
    }

    private async Task RefreshLineAsync()
    {
        if (_closed)
        {
            return;
        }

        var ids = Providers
            .Select(provider => provider.Id)
            .Where(id => ProbeCatalog.Find(id) is not null)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        try
        {
            var draft = await _probeService.RunAsync(ids, _demoMode, CancellationToken.None);
            if (_closed)
            {
                return;
            }

            _lastDraft = draft;
            var history = _lineHistory.Snapshot();
            var snapshot = LineScoring.Classify(draft, history);
            _lineHistory.Add(snapshot);
            var window = _lineHistory.Snapshot();
            foreach (var provider in Providers)
            {
                provider.ApplyLine(snapshot, window);
            }

            if (_selectedProvider is not null && DetailSurface.Visibility == Visibility.Visible)
            {
                ConfigureProviderDetail(_selectedProvider);
            }
        }
        catch
        {
            // Line metrics stay on the last successful probe.
        }
    }

    /// <summary>
    /// 流式单条应用：只新增或更新这一家，不动其他环；数量不变时不同步改高度。
    /// </summary>
    private void ApplySnapshot(QuotaSnapshot snapshot)
    {
        if (_closed)
        {
            return;
        }

        // 刷新中途被取消勾选的 Provider 不再僵尸复活，等它的那次刷新会被 generation 拦掉，
        // 这里再加一道保险。
        if (!ProviderCatalog.Normalize(_settings.EnabledProviderIds).Contains(snapshot.ProviderId))
        {
            return;
        }

        var existing = Providers.FirstOrDefault(provider => provider.Id.Equals(snapshot.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            InsertProviderSorted(new ProviderViewModel(snapshot));
        }
        else
        {
            existing.Apply(snapshot);
            if (_selectedProvider == existing)
            {
                DetailSurface.DataContext = existing;
                ConfigureProviderDetail(existing);
            }
        }

        RelayoutRail(force: false);
    }

    /// <summary>
    /// 环入场：根节点淡入 + 内层内容上浮。hover 缩放用的是根 Border 的
    /// RenderTransform（ScaleTransform），这里只动内层位移，两者不打架。
    /// </summary>
    private void ProviderItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            border.Opacity = 1;
            return;
        }

        border.Opacity = 0;
        if (border.Child is StackPanel panel)
        {
            var slide = new TranslateTransform(0, 10);
            panel.RenderTransform = slide;
            Animate(slide, TranslateTransform.YProperty, 0, 190);
        }

        Animate(border, OpacityProperty, 1, 150);
    }

    private void ProviderItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProviderViewModel provider } surface)
        {
            return;
        }

        ClosePicker();
        ExpandRail();
        AnimateScale(surface, 1.12, 140);
        SelectProvider(provider);
    }

    private void ProviderItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement surface)
        {
            AnimateScale(surface, 1, 140);
        }
    }

    private void ProviderItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProviderViewModel provider } surface)
        {
            return;
        }

        _pressedProvider = provider;
        _pressedSurface = surface;
        AnimateScale(surface, 0.95, 90);
        surface.CaptureMouse();
        e.Handled = true;
    }

    private void ProviderItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedProvider;
        var surface = _pressedSurface;
        _pressedProvider = null;
        _pressedSurface = null;
        if (surface is not null)
        {
            AnimateScale(surface, surface.IsMouseOver ? 1.12 : 1, 130);
            if (surface.IsMouseCaptured)
            {
                surface.ReleaseMouseCapture();
            }
        }

        if (pressed is not null && sender is FrameworkElement { DataContext: ProviderViewModel provider } && provider == pressed)
        {
            SelectProvider(provider);
            _cardPinned = false;
            _detailHoldUntil = DateTimeOffset.UtcNow + ClickDetailHold;
            SyncOutsideClickHook();
            if (!_cardPinned && !IsMouseOver)
            {
                QueueHide();
            }
        }

        e.Handled = true;
    }

    private void ProviderItem_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pressedSurface is null)
        {
            return;
        }

        AnimateScale(_pressedSurface, _pressedSurface.IsMouseOver ? 1.12 : 1, 130);
        _pressedProvider = null;
        _pressedSurface = null;
    }

    private void SelectProvider(ProviderViewModel provider)
    {
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        ClosePicker();
        ExpandRail();
        MaybeRefreshNow();

        if (_selectedProvider == provider)
        {
            ShowDetail();
            return;
        }

        var transition = ++_cardTransitionVersion;
        if (_selectedProvider is not null && DetailSurface.Visibility == Visibility.Visible && DetailSurface.Opacity > 0.05)
        {
            var offset = _settings.Edge == DockEdge.Right ? 7d : -7d;
            Animate(DetailSurface, OpacityProperty, 0, 110, () =>
            {
                if (transition != _cardTransitionVersion)
                {
                    return;
                }

                ConfigureProviderDetail(provider);
                ShowDetail();
            });
            Animate(DetailTranslate, TranslateTransform.XProperty, offset, 110);
            Animate(DetailScale, ScaleTransform.ScaleXProperty, 0.985, 110);
            Animate(DetailScale, ScaleTransform.ScaleYProperty, 0.985, 110);
            return;
        }

        ConfigureProviderDetail(provider);
        ShowDetail();
    }

    private void ConfigureProviderDetail(ProviderViewModel provider)
    {
        _selectedProvider = provider;
        DetailSurface.DataContext = provider;

        var index = Math.Max(0, Providers.IndexOf(provider));
        var ringCenter = RailGeometry.RingCenterY(index);
        var detailHeight = MeasureCardHeight(DetailCard);
        _detailNeed = detailHeight;
        DetailSurface.Height = detailHeight;
        DetailCard.Height = detailHeight;
        RelayoutRail(force: true);
        var surfaceTop = Math.Clamp(ringCenter - detailHeight / 2, 4, Math.Max(4, Stage.Height - detailHeight - 4));
        Canvas.SetTop(DetailSurface, surfaceTop);
        DetailPointer.VerticalAlignment = VerticalAlignment.Top;
        var pointerY = Math.Clamp(ringCenter - surfaceTop - 8, 10, detailHeight - 28);
        DetailPointer.Margin = _settings.Edge == DockEdge.Right
            ? new Thickness(0, pointerY, 8, 0)
            : new Thickness(8, pointerY, 0, 0);
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || !ReferenceEquals(_selectedProvider, provider))
            {
                return;
            }

            var again = MeasureCardHeight(DetailCard);
            if (Math.Abs(again - _detailNeed) <= 2)
            {
                return;
            }

            ConfigureProviderDetail(provider);
        }, DispatcherPriority.Loaded);
    }

    private void ShowDetail()
    {
        if (_selectedProvider is null)
        {
            return;
        }

        _hideTimer.Stop();
        DetailSurface.Visibility = Visibility.Visible;
        ExpandRail();
        var offset = _settings.Edge == DockEdge.Right ? 10d : -10d;
        if (DetailSurface.Opacity < 0.05)
        {
            DetailTranslate.X = offset;
            DetailScale.ScaleX = 0.98;
            DetailScale.ScaleY = 0.98;
        }

        Animate(DetailSurface, OpacityProperty, 1, 180);
        Animate(DetailTranslate, TranslateTransform.XProperty, 0, 180);
        Animate(DetailScale, ScaleTransform.ScaleXProperty, 1, 180);
        Animate(DetailScale, ScaleTransform.ScaleYProperty, 1, 180);
        SyncOutsideClickHook();
    }

    private void HideDetail(bool immediate = false)
    {
        _hideTimer.Stop();
        _cardPinned = false;
        SyncOutsideClickHook();
        if (immediate || !SystemParameters.ClientAreaAnimation)
        {
            DetailSurface.Opacity = 0;
            DetailSurface.Visibility = Visibility.Hidden;
            _detailNeed = 0;
            RelayoutRail(force: true);
            return;
        }

        _cardTransitionVersion++;
        var offset = _settings.Edge == DockEdge.Right ? 8d : -8d;
        var generation = _selectedProvider;
        Animate(DetailSurface, OpacityProperty, 0, 170, () =>
        {
            if (!_cardPinned && generation == _selectedProvider && DetailSurface.Opacity <= 0.01)
            {
                DetailSurface.Visibility = Visibility.Hidden;
                _detailNeed = 0;
                RelayoutRail(force: true);
            }
        });
        Animate(DetailTranslate, TranslateTransform.XProperty, offset, 170);
        Animate(DetailScale, ScaleTransform.ScaleXProperty, 0.985, 170);
        Animate(DetailScale, ScaleTransform.ScaleYProperty, 0.985, 170);
    }

    private static DoubleAnimationUsingKeyFrames Animate(UIElement target, DependencyProperty property, double to, int durationMilliseconds, Action? completed = null)
    {
        try
        {
            var current = (double)target.GetValue(property);
            target.BeginAnimation(property, null);
            target.SetValue(property, current);
            var animation = BuildAnimation(current, to, durationMilliseconds);
            if (completed is not null)
            {
                animation.Completed += (_, _) =>
                {
                    try
                    {
                        completed();
                    }
                    catch
                    {
                        // Completed callbacks must not crash the dispatcher.
                    }
                };
            }
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            return animation;
        }
        catch (InvalidOperationException)
        {
            completed?.Invoke();
            return BuildAnimation(to, to, durationMilliseconds);
        }
    }

    private static DoubleAnimationUsingKeyFrames Animate(Animatable target, DependencyProperty property, double to, int durationMilliseconds, Action? completed = null)
    {
        if (target.IsFrozen)
        {
            completed?.Invoke();
            return BuildAnimation(to, to, durationMilliseconds);
        }

        try
        {
            var current = (double)target.GetValue(property);
            target.BeginAnimation(property, null);
            target.SetValue(property, current);
            var animation = BuildAnimation(current, to, durationMilliseconds);
            if (completed is not null)
            {
                animation.Completed += (_, _) =>
                {
                    try
                    {
                        completed();
                    }
                    catch
                    {
                        // Completed callbacks must not crash the dispatcher.
                    }
                };
            }
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            return animation;
        }
        catch (InvalidOperationException)
        {
            completed?.Invoke();
            return BuildAnimation(to, to, durationMilliseconds);
        }
    }

    private static DoubleAnimationUsingKeyFrames BuildAnimation(double current, double to, int durationMilliseconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMilliseconds)), StrongEaseOut));
        return animation;
    }

    private void ExpandRail(bool immediate = false)
    {
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        if (_railExpanded && !immediate)
        {
            return;
        }

        _railExpanded = true;
        if (immediate || !SystemParameters.ClientAreaAnimation)
        {
            RailScale.ScaleX = 1;
            RailScale.ScaleY = 1;
            ProviderList.Opacity = 1;
            ProviderList.IsHitTestVisible = true;
            ProviderListScale.ScaleX = 1;
            ProviderListScale.ScaleY = 1;
            GearScale.ScaleX = 1;
            GearScale.ScaleY = 1;
            PlusHandle.Opacity = 1;
            PlusHandle.IsHitTestVisible = true;
            DragHandle.Opacity = 1;
            DragHandle.IsHitTestVisible = true;
            PipList.Opacity = 0;
            PlusScale.ScaleX = 1;
            PlusScale.ScaleY = 1;
            return;
        }

        Animate(RailScale, ScaleTransform.ScaleXProperty, 1, 190);
        Animate(RailScale, ScaleTransform.ScaleYProperty, 1, 190);
        ProviderList.IsHitTestVisible = true;
        PlusHandle.IsHitTestVisible = true;
        DragHandle.IsHitTestVisible = true;
        Animate(ProviderList, OpacityProperty, 1, 160);
        Animate(PlusHandle, OpacityProperty, 1, 160);
        Animate(DragHandle, OpacityProperty, 1, 160);
        Animate(PipList, OpacityProperty, 0, 120);
        Animate(ProviderListScale, ScaleTransform.ScaleXProperty, 1, 190);
        Animate(ProviderListScale, ScaleTransform.ScaleYProperty, 1, 190);
        Animate(GearScale, ScaleTransform.ScaleXProperty, 1, 160);
        Animate(GearScale, ScaleTransform.ScaleYProperty, 1, 160);
        Animate(PlusScale, ScaleTransform.ScaleXProperty, 1, 160);
        Animate(PlusScale, ScaleTransform.ScaleYProperty, 1, 160);
    }

    private void CollapseRail(bool immediate = false)
    {
        if ((_cardPinned || _pickerOpen || _isDragging) && !immediate)
        {
            return;
        }

        if (!_railExpanded && !immediate)
        {
            return;
        }

        _railExpanded = false;
        HideDetail(immediate);
        PinCollapsedOrigin();
        if (immediate || !SystemParameters.ClientAreaAnimation)
        {
            RailScale.ScaleX = 0.14;
            RailScale.ScaleY = 0.25;
            ProviderList.Opacity = 0;
            ProviderList.IsHitTestVisible = false;
            ProviderListScale.ScaleX = 0.94;
            ProviderListScale.ScaleY = 0.94;
            GearScale.ScaleX = 0.86;
            GearScale.ScaleY = 0.86;
            PlusHandle.Opacity = 0;
            PlusHandle.IsHitTestVisible = false;
            DragHandle.Opacity = 0;
            DragHandle.IsHitTestVisible = false;
            PipList.Opacity = 1;
            PlusScale.ScaleX = 0.86;
            PlusScale.ScaleY = 0.86;
            return;
        }

        ProviderList.IsHitTestVisible = false;
        PlusHandle.IsHitTestVisible = false;
        DragHandle.IsHitTestVisible = false;
        Animate(ProviderList, OpacityProperty, 0, 90);
        Animate(PlusHandle, OpacityProperty, 0, 90);
        Animate(DragHandle, OpacityProperty, 0, 90);
        Animate(PipList, OpacityProperty, 1, 160);
        Animate(ProviderListScale, ScaleTransform.ScaleXProperty, 0.94, 140);
        Animate(ProviderListScale, ScaleTransform.ScaleYProperty, 0.94, 140);
        Animate(GearScale, ScaleTransform.ScaleXProperty, 0.86, 120);
        Animate(GearScale, ScaleTransform.ScaleYProperty, 0.86, 120);
        Animate(PlusScale, ScaleTransform.ScaleXProperty, 0.86, 120);
        Animate(PlusScale, ScaleTransform.ScaleYProperty, 0.86, 120);
        Animate(RailScale, ScaleTransform.ScaleXProperty, 0.14, 190);
        Animate(RailScale, ScaleTransform.ScaleYProperty, 0.25, 190, () => NativeMethods.RefreshLayeredWindow(this));
    }

    private void EdgeHotZone_MouseEnter(object sender, MouseEventArgs e) => ExpandRail();

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        ExpandRail();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_cardPinned && !_pickerOpen && !_isDragging && !_snapAnimator.IsRunning)
        {
            QueueHide();
        }
    }

    private void DetailSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        ExpandRail();
    }

    private void DetailSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_cardPinned && !_pickerOpen)
        {
            QueueHide();
        }
    }

    private void QueueHide()
    {
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        var holdRemaining = _detailHoldUntil - DateTimeOffset.UtcNow;
        var detailDelay = holdRemaining > DetailLeaveDelay ? holdRemaining : DetailLeaveDelay;
        _hideTimer.Interval = detailDelay;
        _railCollapseTimer.Interval = detailDelay + (RailLeaveDelay - DetailLeaveDelay);
        _hideTimer.Start();
        _railCollapseTimer.Start();
    }

    private void RailBackground_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        _snapAnimator.Stop();
        _snapTarget = null;
        ExpandRail();
        ClosePicker();
        HideDetail(true);
        _isDragging = true;
        _dragMoved = false;
        _dragStartCursor = ScreenPixelsToDip(cursor.X, cursor.Y);
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragSamples.Clear();
        AddDragSample(_dragStartCursor);
        RailShape.CaptureMouse();
        e.Handled = true;
    }

    private void RailBackground_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var point = ScreenPixelsToDip(cursor.X, cursor.Y);
        var delta = point - _dragStartCursor;
        if (!_dragMoved && delta.Length < 4)
        {
            return;
        }

        _dragMoved = true;
        EnsureFloatingChrome();
        Left = _dragStartLeft + point.X - _dragStartCursor.X;
        Top = _dragStartTop + point.Y - _dragStartCursor.Y;
        var screen = FormsScreen.FromPoint(new DrawingPoint(cursor.X, cursor.Y));
        _lastWorkArea = ScreenWorkingAreaInDip(screen);
        AddDragSample(point);
        e.Handled = true;
    }

    private void RailBackground_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        FinishDrag();
        e.Handled = true;
    }

    private void RailBackground_LostMouseCapture(object sender, MouseEventArgs e) => FinishDrag();

    private void FinishDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        var moved = _dragMoved;
        _dragMoved = false;
        if (RailShape.IsMouseCaptured)
        {
            RailShape.ReleaseMouseCapture();
        }

        if (!moved)
        {
            return;
        }

        _buttonActivationBlockedUntil = DateTimeOffset.UtcNow.AddMilliseconds(220);

        try
        {
            var velocity = DragVelocity();
            SnapToNearestEdge(velocity.X, velocity.Y);
        }
        catch
        {
            try
            {
                SnapToCurrentScreen(_settings.Edge);
            }
            catch
            {
                // Stay where the pointer released; the next display change will clamp.
            }
        }
    }

    private void ExitHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Animate(GearScale, ScaleTransform.ScaleXProperty, DragHandle.IsMouseOver ? 1.08 : 1, 120);
        Animate(GearScale, ScaleTransform.ScaleYProperty, DragHandle.IsMouseOver ? 1.08 : 1, 120);
        if (_isDragging || IsButtonActivationBlocked())
        {
            return;
        }

        System.Windows.Application.Current.Shutdown();
        e.Handled = true;
    }

    private void AddDragSample(System.Windows.Point point)
    {
        var now = Stopwatch.GetTimestamp();
        _dragSamples.Add(new DragSample(point, now));
        var cutoff = now - (long)(Stopwatch.Frequency * 0.14);
        _dragSamples.RemoveAll(sample => sample.Timestamp < cutoff);
    }

    private Vector DragVelocity()
    {
        if (_dragSamples.Count < 2)
        {
            return new Vector();
        }

        var first = _dragSamples[0];
        var last = _dragSamples[^1];
        var seconds = (last.Timestamp - first.Timestamp) / (double)Stopwatch.Frequency;
        return seconds <= 0.001 ? new Vector() : (last.Point - first.Point) / seconds;
    }

    private void SnapToNearestEdge(double velocityX, double velocityY)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            SnapToCurrentScreen(_settings.Edge);
            return;
        }

        var screen = FormsScreen.FromPoint(new DrawingPoint(cursor.X, cursor.Y));
        var work = ScreenWorkingAreaInDip(screen);
        if (work.Width <= 1 || work.Height <= 1)
        {
            SnapToCurrentScreen(_settings.Edge);
            return;
        }

        var cursorDip = ScreenPixelsToDip(cursor.X, cursor.Y);
        var projectedX = cursorDip.X + velocityX * 0.12;
        var edge = projectedX < work.Left + work.Width / 2 ? DockEdge.Left : DockEdge.Right;
        SnapToEdge(edge, work, velocityX, velocityY);
    }

    private void SnapToCurrentScreen(DockEdge edge)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var centerPixels = new DrawingPoint(
            (int)Math.Round((Left + Width / 2) * dpi.DpiScaleX),
            (int)Math.Round((Top + Height / 2) * dpi.DpiScaleY));
        var screen = FormsScreen.FromPoint(centerPixels);
        SnapToEdge(edge, ScreenWorkingAreaInDip(screen), 0, 0);
    }

    private void SnapToEdge(DockEdge edge, Rect work, double velocityX, double velocityY)
    {
        CancelChromeTween();
        _lastWorkArea = work;
        _settings.Edge = edge;
        _snapTarget = edge;
        if (_floatingChrome)
        {
            AlignFloatingRailForSnap(edge);
            SetShapeMorph(1, immediate: true);
        }
        ApplyRailFacing(edge);
        var targetLeft = DockedLeft(work, edge);
        var targetTop = ClampTop(work, Top, Height);
        _snapAnimator.Start(targetLeft, targetTop, velocityX, velocityY);
    }

    private void ApplyEdgeLayout(DockEdge edge)
    {
        var cardLeft = edge == DockEdge.Right ? 0d : RailGeometry.Width;
        var railLeft = edge == DockEdge.Right ? RailGeometry.CardColumn : 0d;
        var hotLeft = edge == DockEdge.Right ? RailGeometry.CardColumn + RailGeometry.Width - 12 : 0d;
        var origin = edge == DockEdge.Right ? new System.Windows.Point(1, 0.5) : new System.Windows.Point(0, 0.5);
        var flip = edge == DockEdge.Left;
        Canvas.SetLeft(Rail, railLeft);
        Canvas.SetLeft(EdgeHotZone, hotLeft);
        Canvas.SetLeft(DetailSurface, cardLeft);
        Canvas.SetLeft(PickerSurface, cardLeft);
        Rail.RenderTransformOrigin = origin;
        ApplyRailFacing(edge);
        AlignPanel(DetailSurface, DetailCard, DetailPointer, flip);
        AlignPanel(PickerSurface, PickerCard, PickerPointer, flip);
    }

    private void ApplyRailFacing(DockEdge edge)
    {
        var flip = edge == DockEdge.Left;
        if (_facing == edge && RailShape.RenderTransform is { } current)
        {
            var isFlipped = current is ScaleTransform scale && scale.ScaleX < 0;
            if (isFlipped == flip)
            {
                return;
            }
        }

        _facing = edge;
        RailShape.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        RailShape.RenderTransform = flip ? new ScaleTransform(-1, 1) : Transform.Identity;
    }

    private DockEdge FacingEdgeFromPosition()
    {
        if (_snapTarget is { } target)
        {
            return target;
        }

        if (_lastWorkArea.Width <= 0)
        {
            return _settings.Edge;
        }

        var distLeft = Math.Abs(Left - _lastWorkArea.Left);
        var distRight = Math.Abs(_lastWorkArea.Right - (Left + Width));
        return distLeft <= distRight ? DockEdge.Left : DockEdge.Right;
    }

    private static void AlignPanel(FrameworkElement surface, FrameworkElement card, FrameworkElement pointer, bool leftEdge)
    {
        surface.RenderTransformOrigin = leftEdge ? new System.Windows.Point(0, 0.5) : new System.Windows.Point(1, 0.5);
        card.HorizontalAlignment = leftEdge ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
        pointer.HorizontalAlignment = leftEdge ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        pointer.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        pointer.RenderTransform = leftEdge ? new ScaleTransform(-1, 1) : Transform.Identity;
    }

    private double DockedLeft(Rect work, DockEdge edge)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var overlap = 2.0 / scale;
        return edge == DockEdge.Left
            ? work.Left - overlap
            : work.Right - Width + overlap;
    }

    private void PinCollapsedOrigin()
    {
        if (_floatingChrome)
        {
            return;
        }

        Rail.RenderTransformOrigin = _settings.Edge == DockEdge.Right
            ? new System.Windows.Point(1, 0.5)
            : new System.Windows.Point(0, 0.5);
    }

    private void PlaceAtSavedPosition()
    {
        var screen = FormsScreen.PrimaryScreen ?? FormsScreen.AllScreens[0];
        var work = ScreenWorkingAreaInDip(screen);
        _lastWorkArea = work;
        Left = DockedLeft(work, _settings.Edge);
        Top = ClampTop(work, work.Top + Math.Clamp(_settings.VerticalOffset, 0, 1) * Math.Max(0, work.Height - Height), Height);
    }

    private Rect ScreenWorkingAreaInDip(FormsScreen screen)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new Rect(
            screen.WorkingArea.Left / scaleX,
            screen.WorkingArea.Top / scaleY,
            screen.WorkingArea.Width / scaleX,
            screen.WorkingArea.Height / scaleY);
    }

    private System.Windows.Point ScreenPixelsToDip(int x, int y)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new System.Windows.Point(x / scaleX, y / scaleY);
    }

    private void PersistPosition()
    {
        var available = Math.Max(1, _lastWorkArea.Height - Height);
        _settings.VerticalOffset = Math.Clamp((Top - _lastWorkArea.Top) / available, 0, 1);
        SettingsStore.Save(_settings);
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        try
        {
            if (message is NativeMethods.WmDisplayChange)
            {
                Dispatcher.BeginInvoke(RepositionAfterEnvironmentChange);
                return nint.Zero;
            }

            if (message != NativeMethods.WmNcHitTest || _isDragging)
            {
                return nint.Zero;
            }

            var x = NativeMethods.SignedLowWord(lParam);
            var y = NativeMethods.SignedHighWord(lParam);
            var interactive = IsScreenPointInside(_railExpanded ? Rail : EdgeHotZone, x, y) ||
                              IsVisiblePanel(DetailSurface, x, y) ||
                              IsVisiblePanel(PickerSurface, x, y);
            if (interactive)
            {
                return nint.Zero;
            }

            handled = true;
            return new nint(NativeMethods.HtTransparent);
        }
        catch (InvalidOperationException)
        {
            return nint.Zero;
        }
    }

    private static bool IsScreenPointInside(FrameworkElement element, int x, int y)
    {
        try
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return false;
            }

            var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));
            return x >= topLeft.X && x <= bottomRight.X && y >= topLeft.Y && y <= bottomRight.Y;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool IsVisiblePanel(FrameworkElement element, int x, int y) =>
        element.Visibility == Visibility.Visible && element.Opacity > 0.04 && IsScreenPointInside(element, x, y);

    private void Rail_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        ExpandRail();
    }

    private void ExitHandle_MouseEnter(object sender, MouseEventArgs e)
    {
        ExpandRail();
        Animate(GearScale, ScaleTransform.ScaleXProperty, 1.08, 120);
        Animate(GearScale, ScaleTransform.ScaleYProperty, 1.08, 120);
    }

    private void ExitHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsButtonActivationBlocked())
        {
            Animate(GearScale, ScaleTransform.ScaleXProperty, 0.92, 90);
            Animate(GearScale, ScaleTransform.ScaleYProperty, 0.92, 90);
        }
    }

    private void ExitHandle_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            Animate(GearScale, ScaleTransform.ScaleXProperty, 1, 140);
            Animate(GearScale, ScaleTransform.ScaleYProperty, 1, 140);
        }
    }

    private void PlusHandle_MouseEnter(object sender, MouseEventArgs e)
    {
        ExpandRail();
        Animate(PlusScale, ScaleTransform.ScaleXProperty, 1.08, 120);
        Animate(PlusScale, ScaleTransform.ScaleYProperty, 1.08, 120);
    }

    private void PlusHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsButtonActivationBlocked())
        {
            Animate(PlusScale, ScaleTransform.ScaleXProperty, 0.92, 90);
            Animate(PlusScale, ScaleTransform.ScaleYProperty, 0.92, 90);
        }
    }

    private void PlusHandle_MouseLeave(object sender, MouseEventArgs e)
    {
        Animate(PlusScale, ScaleTransform.ScaleXProperty, 1, 140);
        Animate(PlusScale, ScaleTransform.ScaleYProperty, 1, 140);
    }

    private void PlusHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Animate(PlusScale, ScaleTransform.ScaleXProperty, PlusHandle.IsMouseOver ? 1.08 : 1, 120);
        Animate(PlusScale, ScaleTransform.ScaleYProperty, PlusHandle.IsMouseOver ? 1.08 : 1, 120);
        if (IsButtonActivationBlocked())
        {
            e.Handled = true;
            return;
        }

        if (_pickerOpen)
        {
            ClosePicker();
        }
        else
        {
            OpenPicker();
        }

        e.Handled = true;
    }

    private bool IsButtonActivationBlocked() => DateTimeOffset.UtcNow < _buttonActivationBlockedUntil;

    private void OpenPicker()
    {
        HideDetail(true);
        _cardPinned = false;
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        ExpandRail();
        var height = 8 + 28 + 28 + 2 + 1 + 6 + CatalogItems.Count * 36d + 8;
        PickerSurface.Height = height;
        PickerCard.Height = height;
        RefreshStartupCheck();
        UpdateLabel.Text = "检查更新";
        FitWindowToPicker(height);
        PlacePopup(PickerSurface, PickerPointer, RailGeometry.PlusTop + 20, height);
        ShowPopup(PickerSurface, PickerTranslate, PickerScale);
        _pickerOpen = true;
        SyncOutsideClickHook();
    }

    private void ClosePicker()
    {
        if (!_pickerOpen)
        {
            return;
        }

        _pickerOpen = false;
        HidePopup(PickerSurface, PickerTranslate, PickerScale);
        RestoreWindowAfterPicker();
        SyncOutsideClickHook();
        if (!_cardPinned && !IsMouseOver)
        {
            QueueHide();
        }
    }

    private void StartupItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StartupLaunch.SetEnabled(!StartupLaunch.IsEnabled());
        RefreshStartupCheck();
        e.Handled = true;
    }

    private async void UpdateItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        UpdateLabel.Text = "正在检查…";
        try
        {
            var result = await AppUpdate.CheckAsync(CancellationToken.None);
            UpdateLabel.Text = result.Message;
            if (result.Kind is UpdateKind.Available)
            {
                AppUpdate.Open(result.Url);
            }
        }
        catch
        {
            UpdateLabel.Text = "检查失败";
        }
    }

    private void RefreshStartupCheck()
    {
        StartupCheckMark.Text = StartupLaunch.IsEnabled() ? "✓" : string.Empty;
    }

    private void CatalogItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CatalogItemViewModel item })
        {
            return;
        }

        if (item.IsEnabled && CatalogItems.Count(entry => entry.IsEnabled) <= 1)
        {
            return;
        }

        item.IsEnabled = !item.IsEnabled;
        _settings.EnabledProviderIds = ProviderCatalog.Ordered(CatalogItems.Where(entry => entry.IsEnabled).Select(entry => entry.Id)).ToList();
        SettingsStore.Save(_settings);
        SyncProvidersToSettings();
        _ = RefreshAsync();
        e.Handled = true;
    }

    /// <summary>
    /// 勾选变化后立刻同步窄轨：去掉的环淡出收起，新加的环按目录顺序插入，
    /// 先用本地缓存（或 Loading 占位）顶上，不等网络刷新回来。
    /// </summary>
    private void SyncProvidersToSettings()
    {
        if (_closed)
        {
            return;
        }

        var enabled = ProviderCatalog.Normalize(_settings.EnabledProviderIds);
        var added = false;
        for (var index = Providers.Count - 1; index >= 0; index--)
        {
            if (!enabled.Contains(Providers[index].Id))
            {
                if (_selectedProvider == Providers[index])
                {
                    _selectedProvider = null;
                    HideDetail(true);
                }

                RemoveProviderAnimated(Providers[index]);
            }
        }

        var missing = enabled.Where(id => Providers.All(provider => !provider.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0)
        {
            // 只读本地缓存，不碰网络；没有缓存就是 Loading 态，等流式 RefreshAsync 回填。
            // 退场中的 id 仍在集合里，不会重复添加；若用户在退场动画内勾回来，
            // FinishRemoveProvider 会检测到已重新启用并还原因子。
            foreach (var snapshot in _quotaService.LoadingSnapshots(missing))
            {
                InsertProviderSorted(new ProviderViewModel(snapshot));
                added = true;
            }
        }

        if (added)
        {
            // 新增和窗口长高同步走缓动；移除走先收环、再缩窗口的两段式，见 RemoveProviderAnimated。
            RelayoutRail(force: false);
        }
    }

    private void InsertProviderSorted(ProviderViewModel viewModel)
    {
        var rank = CatalogRank(viewModel.Id);
        var index = Providers.Count(existing => CatalogRank(existing.Id) < rank);
        Providers.Insert(Math.Clamp(index, 0, Providers.Count), viewModel);
    }

    private static int CatalogRank(string id)
    {
        var index = 0;
        foreach (var definition in ProviderCatalog.All)
        {
            if (definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            index++;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// 环退场（单时钟并行）：容器高度、窗口高度、有效个数（手柄位置）同一个进度一起走，
    /// × 和窗口底边全程贴住，没有“× 先跑、底边后追”的两段滞后。
    /// 退场槽位只有一个：200ms 内连点第二个时，第二个走 helper 动画做近似收起，不抢驱动。
    /// </summary>
    private void RemoveProviderAnimated(ProviderViewModel viewModel)
    {
        if (!_removingIds.Add(viewModel.Id))
        {
            return;
        }

        if (ProviderList.ItemContainerGenerator.ContainerFromItem(viewModel) is not FrameworkElement container ||
            !SystemParameters.ClientAreaAnimation)
        {
            CompleteRemoval(viewModel, relayout: true);
            return;
        }

        if (_exitingContainer is null)
        {
            container.IsHitTestVisible = false;
            Animate(container, OpacityProperty, 0, 130);
            _exitingContainer = container;
            _exitingViewModel = viewModel;
            _exitFromH = container.ActualHeight > 1 ? container.ActualHeight : RailGeometry.ItemPitch;
            StartChromeTween(RailTargetHeight(), TargetProviderCount(), 200, viewModel.Id, () => CompleteRemoval(viewModel, relayout: true));
            return;
        }

        // 退场驱动正忙：近似收起，不抢单时钟。
        container.IsHitTestVisible = false;
        Animate(container, OpacityProperty, 0, 130);
        var fromHeight = container.ActualHeight > 1 ? container.ActualHeight : RailGeometry.ItemPitch;
        container.BeginAnimation(FrameworkElement.HeightProperty, null);
        container.SetValue(FrameworkElement.HeightProperty, fromHeight);
        Animate(container, FrameworkElement.HeightProperty, 0, 170, () => CompleteRemoval(viewModel, relayout: true));
    }

    private void CompleteRemoval(ProviderViewModel viewModel, bool relayout)
    {
        _removingIds.Remove(viewModel.Id);
        if (_exitingViewModel == viewModel)
        {
            _exitingViewModel = null;
            _exitingContainer = null;
        }

        if (!Providers.Contains(viewModel))
        {
            return;
        }

        // 退场中途又被勾选回来：还原因子，不移除。
        if (ProviderCatalog.Normalize(_settings.EnabledProviderIds).Contains(viewModel.Id))
        {
            RestoreProviderContainer(viewModel);
            return;
        }

        Providers.Remove(viewModel);
        if (relayout)
        {
            RelayoutRail(force: false);
        }
    }

    /// <summary>
    /// 退场中途又被勾回来：容器高度与透明度播回去，同时布局有效个数缓回当前数量。
    /// </summary>
    private void RestoreProviderContainer(ProviderViewModel viewModel)
    {
        _removingIds.Remove(viewModel.Id);
        _chromeCompletions.Remove(viewModel.Id);
        if (_exitingViewModel == viewModel)
        {
            _exitingViewModel = null;
            _exitingContainer = null;
        }

        if (ProviderList.ItemContainerGenerator.ContainerFromItem(viewModel) is not FrameworkElement container)
        {
            RelayoutRail(force: false);
            return;
        }

        container.IsHitTestVisible = true;
        Animate(container, OpacityProperty, 1, 150);
        Animate(container, FrameworkElement.HeightProperty, RailGeometry.ItemPitch, 170, () =>
        {
            if (ProviderList.ItemContainerGenerator.ContainerFromItem(viewModel) is FrameworkElement live &&
                live == container)
            {
                container.BeginAnimation(FrameworkElement.HeightProperty, null);
                container.ClearValue(FrameworkElement.HeightProperty);
            }
        });
        StartChromeTween(Height, TargetProviderCount(), 170);
    }

    private void FitWindowToPicker(double pickerHeight)
    {
        CancelChromeTween();
        _topBeforePicker = Top;
        var needed = Math.Max(RailGeometry.HeightFor(Providers.Count), pickerHeight + 8);
        if (needed <= Height + 0.5)
        {
            return;
        }

        Height = needed;
        Stage.Height = needed;
        if (_lastWorkArea.Height > 0)
        {
            Top = ClampTop(_lastWorkArea, Top, Height);
        }
    }

    private void RestoreWindowAfterPicker()
    {
        RelayoutRail(force: true);
        if (_lastWorkArea.Height > 0)
        {
            Top = ClampTop(_lastWorkArea, _topBeforePicker, Height);
        }
    }

    private void PlacePopup(FrameworkElement surface, FrameworkElement pointer, double targetCenter, double height)
    {
        var top = Math.Clamp(targetCenter - height / 2, 4, Math.Max(4, Stage.Height - height - 4));
        Canvas.SetTop(surface, top);
        var pointerY = Math.Clamp(targetCenter - top - 8, 10, height - 28);
        pointer.Margin = _settings.Edge == DockEdge.Right
            ? new Thickness(0, pointerY, 8, 0)
            : new Thickness(8, pointerY, 0, 0);
    }

    private void ShowPopup(UIElement surface, TranslateTransform translate, ScaleTransform scale)
    {
        surface.Visibility = Visibility.Visible;
        var offset = _settings.Edge == DockEdge.Right ? 10d : -10d;
        if (surface.Opacity < 0.05)
        {
            translate.X = offset;
            scale.ScaleX = 0.98;
            scale.ScaleY = 0.98;
        }

        Animate(surface, OpacityProperty, 1, 180);
        Animate(translate, TranslateTransform.XProperty, 0, 180);
        Animate(scale, ScaleTransform.ScaleXProperty, 1, 180);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, 180);
    }

    private void HidePopup(UIElement surface, TranslateTransform translate, ScaleTransform scale)
    {
        var offset = _settings.Edge == DockEdge.Right ? 8d : -8d;
        Animate(surface, OpacityProperty, 0, 140, () =>
        {
            if (surface.Opacity <= 0.01)
            {
                surface.Visibility = Visibility.Hidden;
            }
        });
        Animate(translate, TranslateTransform.XProperty, offset, 140);
        Animate(scale, ScaleTransform.ScaleXProperty, 0.985, 140);
        Animate(scale, ScaleTransform.ScaleYProperty, 0.985, 140);
    }

    private void ApplyOfficial(IReadOnlyDictionary<string, OfficialStatus> official)
    {
        if (_closed || _lastDraft is null)
        {
            return;
        }

        _lastDraft = new ProbeSnapshot
        {
            At = _lastDraft.At,
            Client = _lastDraft.Client,
            Endpoints = _lastDraft.Endpoints,
            PublicIp = _lastDraft.PublicIp,
            Location = _lastDraft.Location,
            DirectOverseasOk = _lastDraft.DirectOverseasOk,
            Official = official
        };
        var snapshot = LineScoring.Classify(_lastDraft, _lineHistory.Snapshot());
        var window = _lineHistory.Snapshot();
        foreach (var provider in Providers)
        {
            provider.ApplyLine(snapshot, window);
        }

        if (_selectedProvider is not null && DetailSurface.Visibility == Visibility.Visible)
        {
            ConfigureProviderDetail(_selectedProvider);
        }
    }

    private static double MeasureCardHeight(Border card)
    {
        card.BeginAnimation(HeightProperty, null);
        card.ClearValue(HeightProperty);
        card.InvalidateMeasure();
        card.Measure(new System.Windows.Size(176, 4000));
        var inner = card.Child as FrameworkElement;
        inner?.Measure(new System.Windows.Size(Math.Max(1, 176 - card.Padding.Left - card.Padding.Right), 4000));
        var innerHeight = inner is null ? 0 : inner.DesiredSize.Height + card.Padding.Top + card.Padding.Bottom;
        return Math.Ceiling(Math.Max(1, Math.Max(card.DesiredSize.Height, innerHeight)));
    }

    private void RelayoutRail(bool force = true)
    {
        var target = RailTargetHeight();
        if (force || !SystemParameters.ClientAreaAnimation)
        {
            CancelChromeTween();
            _countFrom = _countTo = TargetProviderCount();
            _countT = 1;
            LayoutRailChrome(target);
            if (_lastWorkArea.Width > 0 && !_floatingChrome)
            {
                Top = ClampTop(_lastWorkArea, Top, target);
                Left = DockedLeft(_lastWorkArea, _settings.Edge);
            }

            return;
        }

        StartChromeTween(target, TargetProviderCount(), 200);
    }

    /// <summary>
    /// 布局目标个数：集合数量减去正在退场（淡出收起中、尚未摘除）的，
    /// 这样连续勾选/流式回填重定目标时不会和退场动画打架。
    /// </summary>
    private int TargetProviderCount()
    {
        var exiting = 0;
        foreach (var provider in Providers)
        {
            if (_removingIds.Contains(provider.Id))
            {
                exiting++;
            }
        }

        return Math.Max(0, Providers.Count - exiting);
    }

    /// <summary>
    /// Picker 打开时窗口至少要装下 Picker 卡，否则收缩窄轨会把 Picker 下半裁掉；
    /// 关 Picker 时 RestoreWindowAfterPicker 会按纯轨道高度缩回去。
    /// </summary>
    private double RailTargetHeight()
    {
        var target = RailGeometry.HeightFor(TargetProviderCount());
        if (_pickerOpen)
        {
            target = Math.Max(target, PickerSurface.Height + 16);
        }

        if (_detailNeed > 1)
        {
            target = Math.Max(target, _detailNeed + 16);
        }

        return target;
    }

    private void LayoutRailChrome(double height)
    {
        // 有效个数是连续量：缓动过程中手柄/列表跟着当前帧走，而不是在首帧就跳到整数终点。
        // （之前 × 按钮首帧瞬移 72px，就是减环时下半闪动的来源。）稳态时它恒等于整数个数。
        var effCount = CurrentEffCount();
        var railHeight = RailGeometry.EdgePad + RailGeometry.EndControl +
            effCount * RailGeometry.ItemPitch + RailGeometry.EndControl + RailGeometry.EdgePad;
        Height = height;
        Width = RailGeometry.CardColumn + RailGeometry.Width;
        Stage.Width = Width;
        Stage.Height = height;
        Rail.Height = railHeight;
        RailShape.Width = RailGeometry.Width;
        RailShape.Height = railHeight;
        ApplyRailPath();
        Canvas.SetLeft(PlusHandle, 9);
        Canvas.SetTop(PlusHandle, RailGeometry.PlusTop + 4);
        Canvas.SetTop(ProviderList, RailGeometry.ListTop);
        ProviderList.Height = Math.Max(RailGeometry.ItemPitch, RailGeometry.ItemPitch * effCount);
        Canvas.SetTop(PipList, RailGeometry.ListTop);
        PipList.Height = Math.Max(RailGeometry.ItemPitch, RailGeometry.ItemPitch * effCount);
        Canvas.SetLeft(DragHandle, 9);
        Canvas.SetTop(DragHandle, RailGeometry.EdgePad + RailGeometry.EndControl + RailGeometry.ItemPitch * effCount + 4);
        EdgeHotZone.Height = 58;
        Canvas.SetTop(EdgeHotZone, Math.Max(0, (railHeight - 58) / 2));
        if (!_floatingChrome)
        {
            ApplyEdgeLayout(_settings.Edge);
            if (_lastWorkArea.Width > 0)
            {
                Left = DockedLeft(_lastWorkArea, _settings.Edge);
            }
        }
    }

    private double CurrentEffCount() => _countFrom + (_countTo - _countFrom) * Math.Clamp(_countT, 0, 1);

    /// <summary>
    /// 窗口高度、Top、有效个数、退场容器高度四通道同一时钟缓动。
    /// 顺路重定（流式回填、连续勾选的目标没变）直接让路不停，只重置速度；
    /// 真正换目标才从当前帧续跑，位置永远连续，不会闪跳。
    /// </summary>
    private void StartChromeTween(double targetHeight, double toCount, int durationMs, string? completionKey = null, Action? completed = null)
    {
        if (_isDragging || _floatingChrome)
        {
            return;
        }

        var targetTop = _lastWorkArea.Width > 0 ? ClampTop(_lastWorkArea, Top, targetHeight) : Top;
        if (_chromeTweenActive &&
            Math.Abs(_chromeToH - targetHeight) < 0.5 &&
            Math.Abs(_chromeToC - toCount) < 0.01 &&
            Math.Abs(_chromeToT - targetTop) < 0.5)
        {
            return;
        }

        if (Math.Abs(Height - targetHeight) < 0.5 &&
            Math.Abs(CurrentEffCount() - toCount) < 0.01 &&
            Math.Abs(Top - targetTop) < 0.5)
        {
            return;
        }

        _chromeFromH = Height;
        _chromeToH = targetHeight;
        _chromeFromT = Top;
        _chromeToT = targetTop;
        _chromeFromC = CurrentEffCount();
        _chromeToC = toCount;
        if (_exitingContainer is not null)
        {
            _exitFromH = ReadContainerHeight(_exitingContainer);
        }

        if (completionKey is not null && completed is not null)
        {
            _chromeCompletions[completionKey] = completed;
        }

        _chromeMs = Math.Max(1, durationMs);
        _chromeStart = Stopwatch.GetTimestamp();
        if (!_chromeTweenActive)
        {
            _chromeTweenActive = true;
            CompositionTarget.Rendering += OnChromeTweenFrame;
        }
    }

    private static double ReadContainerHeight(FrameworkElement container)
    {
        if (container.GetValue(FrameworkElement.HeightProperty) is double value && double.IsFinite(value) && value > 0)
        {
            return value;
        }

        return container.ActualHeight > 1 ? container.ActualHeight : RailGeometry.ItemPitch;
    }

    private void OnChromeTweenFrame(object? sender, EventArgs e)
    {
        if (_isDragging || _floatingChrome)
        {
            CancelChromeTween();
            return;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - _chromeStart) * 1000.0 / Stopwatch.Frequency;
        var t = Math.Clamp(elapsedMs / _chromeMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        Height = _chromeFromH + (_chromeToH - _chromeFromH) * eased;
        Top = _chromeFromT + (_chromeToT - _chromeFromT) * eased;
        _countFrom = _chromeFromC;
        _countTo = _chromeToC;
        _countT = eased;
        if (_exitingContainer is not null)
        {
            _exitingContainer.SetValue(FrameworkElement.HeightProperty, _exitFromH * (1 - eased));
        }

        LayoutRailChrome(Height);
        if (t >= 1)
        {
            Height = _chromeToH;
            Top = _chromeToT;
            _countFrom = _countTo = _chromeToC;
            _countT = 1;
            if (_exitingContainer is not null)
            {
                _exitingContainer.SetValue(FrameworkElement.HeightProperty, 0d);
            }

            LayoutRailChrome(Height);
            _chromeTweenActive = false;
            CompositionTarget.Rendering -= OnChromeTweenFrame;
            var completions = _chromeCompletions.Values.ToArray();
            _chromeCompletions.Clear();
            foreach (var completion in completions)
            {
                try
                {
                    completion();
                }
                catch
                {
                    // 完成回调不允许炸掉 dispatcher。
                }
            }
        }
    }

    private void CancelChromeTween()
    {
        if (!_chromeTweenActive)
        {
            return;
        }

        _chromeTweenActive = false;
        CompositionTarget.Rendering -= OnChromeTweenFrame;
        // 拖拽/吸附/开关 Picker 打断退场：瞬间结算（摘除或还原），不等动画，
        // 后续布局由打断方（吸附完成、Picker 关闭等）接管愈合。
        _chromeCompletions.Clear();
        SettleExitingContainer();
    }

    /// <summary>
    /// 缓动被打断时退场容器的瞬间结算：该摘摘、该还还，不做动画、不碰布局。
    /// </summary>
    private void SettleExitingContainer()
    {
        var container = _exitingContainer;
        var viewModel = _exitingViewModel;
        _exitingContainer = null;
        _exitingViewModel = null;
        if (container is null || viewModel is null)
        {
            return;
        }

        _removingIds.Remove(viewModel.Id);
        if (!Providers.Contains(viewModel))
        {
            return;
        }

        if (ProviderCatalog.Normalize(_settings.EnabledProviderIds).Contains(viewModel.Id))
        {
            container.BeginAnimation(OpacityProperty, null);
            container.Opacity = 1;
            container.BeginAnimation(FrameworkElement.HeightProperty, null);
            container.ClearValue(FrameworkElement.HeightProperty);
            container.IsHitTestVisible = true;
            return;
        }

        Providers.Remove(viewModel);
    }

    private void EnsureFloatingChrome()
    {
        CancelChromeTween();
        if (_floatingChrome)
        {
            return;
        }

        _floatingChrome = true;
        Rail.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        ApplyRailFacing(_settings.Edge);
        SetShapeMorph(1, immediate: true);
    }

    private void RestoreDockedChrome(DockEdge edge)
    {
        _floatingChrome = false;
        Width = RailGeometry.CardColumn + RailGeometry.Width;
        Stage.Width = Width;
        ApplyEdgeLayout(edge);
    }

    private void AlignFloatingRailForSnap(DockEdge edge)
    {
        var current = Canvas.GetLeft(Rail);
        if (!double.IsFinite(current))
        {
            current = _settings.Edge == DockEdge.Right ? RailGeometry.CardColumn : 0;
        }

        var desired = edge == DockEdge.Right ? RailGeometry.CardColumn : 0;
        if (Math.Abs(current - desired) < 0.01)
        {
            return;
        }

        // Keep the rail under the pointer while changing which side of the fixed-size HWND it occupies.
        Left += current - desired;
        Canvas.SetLeft(Rail, desired);
    }

    private void UpdateFloatingShape()
    {
        var edge = FacingEdgeFromPosition();
        ApplyRailFacing(edge);
        SetShapeMorph(MorphFromPosition(edge), immediate: true);
    }

    private double MorphFromPosition(DockEdge edge)
    {
        if (_lastWorkArea.Width <= 0)
        {
            return 0;
        }

        var distance = edge == DockEdge.Left
            ? Math.Abs(Left - _lastWorkArea.Left)
            : Math.Abs(_lastWorkArea.Right - (Left + Width));
        var t = Math.Clamp(distance / 56, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private void SetShapeMorph(double to, bool immediate)
    {
        to = Math.Clamp(to, 0, 1);
        System.Windows.Media.CompositionTarget.Rendering -= OnShapeMorphFrame;
        if (immediate)
        {
            _shapeMorph = to;
            ApplyRailPath();
            return;
        }

        _shapeMorphFrom = _shapeMorph;
        _shapeMorphTo = to;
        _shapeMorphStart = Stopwatch.GetTimestamp();
        System.Windows.Media.CompositionTarget.Rendering += OnShapeMorphFrame;
    }

    private void OnShapeMorphFrame(object? sender, EventArgs e)
    {
        var elapsed = (Stopwatch.GetTimestamp() - _shapeMorphStart) / (double)Stopwatch.Frequency;
        var t = Math.Clamp(elapsed / 0.22, 0, 1);
        var eased = t * t * (3 - 2 * t);
        _shapeMorph = _shapeMorphFrom + (_shapeMorphTo - _shapeMorphFrom) * eased;
        ApplyRailPath();
        if (t >= 1)
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnShapeMorphFrame;
        }
    }

    private void ApplyRailPath()
    {
        var height = Rail.Height;
        if (!double.IsFinite(height) || height < 8)
        {
            return;
        }

        try
        {
            var data = RailGeometry.BuildPath(height, _shapeMorph);
            if (data == _railPathData && RailShape.Data is not null)
            {
                return;
            }

            var geometry = Geometry.Parse(data);
            if (geometry.CanFreeze)
            {
                geometry.Freeze();
            }

            RailShape.Data = geometry;
            _railPathData = data;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            // Keep the last valid silhouette.
        }
    }

    private static double ClampTop(Rect work, double top, double height)
    {
        var min = work.Top;
        var max = Math.Max(work.Top, work.Bottom - height);
        var clamped = Math.Clamp(double.IsFinite(top) ? top : min, min, max);
        return double.IsFinite(clamped) ? clamped : min;
    }

    private void AnimateScale(UIElement element, double to, int duration)
    {
        var scale = EnsureMutableScale(element);
        Animate(scale, ScaleTransform.ScaleXProperty, to, duration);
        Animate(scale, ScaleTransform.ScaleYProperty, to, duration);
    }

    private static ScaleTransform EnsureMutableScale(UIElement element)
    {
        if (element.RenderTransform is ScaleTransform current && !current.IsFrozen)
        {
            return current;
        }

        var x = 1d;
        var y = 1d;
        if (element.RenderTransform is ScaleTransform existing)
        {
            x = existing.ScaleX;
            y = existing.ScaleY;
        }

        var scale = new ScaleTransform(x, y);
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        element.RenderTransform = scale;
        return scale;
    }

    private void RepositionAfterEnvironmentChange()
    {
        if (_closed || !IsLoaded || _isDragging || _floatingChrome)
        {
            return;
        }

        try
        {
            var screens = FormsScreen.AllScreens;
            if (screens.Length == 0)
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
            var centerPixels = new DrawingPoint(
                (int)Math.Round((Left + Width / 2) * scaleX),
                (int)Math.Round((Top + Height / 2) * scaleY));
            var screen = FormsScreen.FromPoint(centerPixels) ?? FormsScreen.PrimaryScreen ?? screens[0];
            var work = ScreenWorkingAreaInDip(screen);
            if (work.Width <= 1 || work.Height <= 1)
            {
                return;
            }

            _lastWorkArea = work;
            Left = DockedLeft(work, _settings.Edge);
            Top = ClampTop(work, Top, Height);
            NativeMethods.RefreshLayeredWindow(this);
        }
        catch
        {
            // Display topology can be transient while a monitor is plugged or unplugged.
        }
    }

    private void MaybeRefreshNow()
    {
        if (DateTimeOffset.Now - _lastRefreshAt < TimeSpan.FromSeconds(45))
        {
            return;
        }

        _ = RefreshAsync();
    }

    private void OnOutsideMouseDown(int x, int y)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || (!_pickerOpen && !_cardPinned))
            {
                return;
            }

            if (_pickerOpen && (IsScreenPointInside(PickerSurface, x, y) || IsScreenPointInside(PlusHandle, x, y)))
            {
                return;
            }

            if (_cardPinned && IsScreenPointInside(DetailSurface, x, y))
            {
                return;
            }

            ClosePicker();
            if (_cardPinned)
            {
                _cardPinned = false;
                HideDetail();
            }
        });
    }

    private void SyncOutsideClickHook()
    {
        if (_pickerOpen || _cardPinned)
        {
            _outsideClick.Start();
        }
        else
        {
            _outsideClick.Stop();
        }
    }

    private sealed record DragSample(System.Windows.Point Point, long Timestamp);
}
