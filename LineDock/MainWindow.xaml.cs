using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LineDock.Models;
using LineDock.Native;
using LineDock.Services;
using LineDock.ViewModels;
using FormsScreen = System.Windows.Forms.Screen;
using DrawingPoint = System.Drawing.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;

namespace LineDock;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly KeySpline StrongEaseOut = new(0.23, 1, 0.32, 1);
    private static readonly TimeSpan DetailLeaveDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RailLeaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ClickDetailHold = TimeSpan.FromMilliseconds(480);
    private static readonly TimeSpan PostSnapCollapseDelay = TimeSpan.FromMilliseconds(180);
    private readonly AppSettings _settings;
    private readonly ProbeService _probeService = new();
    private readonly HistoryStore _history = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _railCollapseTimer;
    private readonly DispatcherTimer _recoveryRefreshTimer;
    private readonly SpringWindowAnimator _snapAnimator;
    private readonly OutsideClickWatcher _outsideClick = new();
    private readonly bool _demoMode;
    private readonly bool _motionPreview;
    private readonly bool _openMenuOnLoad;
    private readonly List<DragSample> _dragSamples = [];
    private CancellationTokenSource? _refreshCancellation;
    private int _refreshGeneration;
    private bool _closed;
    private string? _railPathData;
    private HwndSource? _windowSource;
    private LineViewModel? _selectedLine;
    private LineViewModel? _pressedLine;
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
    private bool _railExpanded;
    private int _cardTransitionVersion;
    private DateTimeOffset _lastRefreshAt;
    private DateTimeOffset _buttonActivationBlockedUntil;
    private DateTimeOffset _detailHoldUntil;
    private System.Windows.Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double _topBeforeOverlay;
    private bool _overlayGrew;
    private Rect _lastWorkArea;
    private string _intervalLabel = "探测间隔  10 秒";
    private ProbeSnapshot? _lastDraft;
    private double _detailNeed;

    public MainWindow(AppSettings settings, bool demoMode, bool motionPreview = false, bool openMenuOnLoad = false)
    {
        _settings = settings;
        _demoMode = demoMode;
        _motionPreview = motionPreview;
        _openMenuOnLoad = openMenuOnLoad;
        InitializeComponent();
        DataContext = this;

        Lines = new ObservableCollection<LineViewModel>(
            ProbeCatalog.Selected(_settings.EnabledTargetIds).Select(target => new LineViewModel(target)));
        CatalogItems = new ObservableCollection<CatalogItemViewModel>(
            ProbeCatalog.Overseas.Select(item => new CatalogItemViewModel(
                item.Id,
                item.Label,
                _settings.EnabledTargetIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))));
        IntervalLabel = $"探测间隔  {_settings.IntervalSeconds} 秒";
        _outsideClick.ButtonDown += OnOutsideMouseDown;
        _probeService.OfficialReady += official => Dispatcher.BeginInvoke(() => ApplyOfficial(official));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.IntervalSeconds) };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
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
            LayoutRailChrome();
            if (!_pickerOpen && !IsMouseOver)
            {
                _railCollapseTimer.Stop();
                _railCollapseTimer.Interval = PostSnapCollapseDelay;
                _railCollapseTimer.Start();
            }
        };
    }

    public ObservableCollection<LineViewModel> Lines { get; }

    private int LineCount => Math.Max(1, Lines.Count);
    public ObservableCollection<CatalogItemViewModel> CatalogItems { get; }

    public string IntervalLabel
    {
        get => _intervalLabel;
        set
        {
            _intervalLabel = value;
            OnPropertyChanged();
        }
    }

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
        _history.Load();
        LayoutRailChrome();
        PlaceAtSavedPosition();
        CollapseRail(true);
        _refreshTimer.Start();
        try
        {
            await RefreshAsync();
        }
        catch
        {
            // First paint must not depend on a successful probe.
        }

        if (_motionPreview)
        {
            await RunMotionPreviewAsync();
        }

        if (_openMenuOnLoad)
        {
            ExpandRail(true);
            OpenPicker();
        }
    }

    private async Task RunMotionPreviewAsync()
    {
        await Task.Delay(420);
        ExpandRail();
        await Task.Delay(240);
        if (Lines.Count > 0)
        {
            SelectLine(Lines[0]);
        }

        await Task.Delay(720);
        if (Lines.Count > 1)
        {
            SelectLine(Lines[1]);
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
        CrashLog.WriteLine("window closed");
        _refreshGeneration++;
        SystemEvents.DisplaySettingsChanged -= OnSystemDisplayChanged;
        SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _refreshTimer.Stop();
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        _recoveryRefreshTimer.Stop();
        _snapAnimator.Stop();
        System.Windows.Media.CompositionTarget.Rendering -= OnShapeMorphFrame;
        _refreshCancellation?.Cancel();
        _history.Save();
        _probeService.Dispose();
        _refreshCancellation?.Dispose();
        _outsideClick.Dispose();
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
            var draft = await _probeService.RunAsync(_settings.EnabledTargetIds, _demoMode, token);
            if (_closed || generation != _refreshGeneration || token.IsCancellationRequested)
            {
                return;
            }

            _lastDraft = draft;
            var history = _history.Snapshot();
            var snapshot = LineScoring.Classify(draft, history);
            _history.Add(snapshot);
            var window = _history.Snapshot();
            foreach (var line in Lines)
            {
                line.Apply(snapshot, window);
            }

            if (_selectedLine is not null)
            {
                ConfigureLineDetail(_selectedLine);
            }

            _lastRefreshAt = DateTimeOffset.Now;
            _history.Save();
        }
        catch (OperationCanceledException)
        {
            // A newer probe replaced this one.
        }
        catch (Exception)
        {
            // Keep the last successful snapshot on screen.
        }
    }

    private void LineItem_Loaded(object sender, RoutedEventArgs e)
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

    private void LineItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LineViewModel line } surface)
        {
            return;
        }

        ClosePicker();
        ExpandRail();
        AnimateScale(surface, 1.12, 140);
        SelectLine(line);
    }

    private void LineItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement surface)
        {
            AnimateScale(surface, 1, 140);
        }
    }

    private void LineItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LineViewModel line } surface)
        {
            return;
        }

        _pressedLine = line;
        _pressedSurface = surface;
        AnimateScale(surface, 0.95, 90);
        surface.CaptureMouse();
        e.Handled = true;
    }

    private void LineItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedLine;
        var surface = _pressedSurface;
        _pressedLine = null;
        _pressedSurface = null;
        if (surface is not null)
        {
            AnimateScale(surface, surface.IsMouseOver ? 1.12 : 1, 130);
            if (surface.IsMouseCaptured)
            {
                surface.ReleaseMouseCapture();
            }
        }

        if (pressed is not null && sender is FrameworkElement { DataContext: LineViewModel line } && line == pressed)
        {
            SelectLine(line);
            _cardPinned = false;
            _detailHoldUntil = DateTimeOffset.UtcNow + ClickDetailHold;
            SyncOutsideClickHook();
            _ = RefreshAsync();
            if (!_cardPinned && !IsMouseOver)
            {
                QueueHide();
            }
        }

        e.Handled = true;
    }

    private void LineItem_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pressedSurface is null)
        {
            return;
        }

        AnimateScale(_pressedSurface, _pressedSurface.IsMouseOver ? 1.12 : 1, 130);
        _pressedLine = null;
        _pressedSurface = null;
    }

    private void SelectLine(LineViewModel line)
    {
        _hideTimer.Stop();
        _railCollapseTimer.Stop();
        ClosePicker();
        ExpandRail();

        if (_selectedLine == line)
        {
            ShowDetail();
            return;
        }

        var transition = ++_cardTransitionVersion;
        if (_selectedLine is not null && DetailSurface.Visibility == Visibility.Visible && DetailSurface.Opacity > 0.05)
        {
            var offset = _settings.Edge == DockEdge.Right ? 7d : -7d;
            Animate(DetailSurface, OpacityProperty, 0, 110, () =>
            {
                if (transition != _cardTransitionVersion)
                {
                    return;
                }

                ConfigureLineDetail(line);
                ShowDetail();
            });
            Animate(DetailTranslate, TranslateTransform.XProperty, offset, 110);
            Animate(DetailScale, ScaleTransform.ScaleXProperty, 0.985, 110);
            Animate(DetailScale, ScaleTransform.ScaleYProperty, 0.985, 110);
            return;
        }

        ConfigureLineDetail(line);
        ShowDetail();
    }

    private void ConfigureLineDetail(LineViewModel line)
    {
        _selectedLine = line;
        DetailSurface.DataContext = line;
        var index = Math.Max(0, Lines.IndexOf(line));
        var ringCenter = RailGeometry.RingCenterY(index);
        var detailHeight = MeasureCardHeight(DetailCard);
        _detailNeed = detailHeight;
        DetailCard.Height = detailHeight;
        DetailSurface.Height = detailHeight;
        FitWindowToCard(detailHeight);
        var surfaceTop = Math.Clamp(ringCenter - detailHeight / 2, 4, Math.Max(4, Stage.Height - detailHeight - 4));
        Canvas.SetTop(DetailSurface, surfaceTop);
        var pointerY = Math.Clamp(ringCenter - surfaceTop - 8, 10, Math.Max(28, detailHeight - 28));
        DetailPointer.Margin = _settings.Edge == DockEdge.Right
            ? new Thickness(0, pointerY, 8, 0)
            : new Thickness(8, pointerY, 0, 0);
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || !ReferenceEquals(_selectedLine, line))
            {
                return;
            }

            var again = MeasureCardHeight(DetailCard);
            if (Math.Abs(again - _detailNeed) <= 2)
            {
                return;
            }

            ConfigureLineDetail(line);
        }, DispatcherPriority.Loaded);
    }

    private void ShowDetail()
    {
        if (_selectedLine is null)
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
        _detailNeed = 0;
        if (!_pickerOpen)
        {
            ShrinkToRail(keepCurrentTop: true);
        }

        if (immediate || !SystemParameters.ClientAreaAnimation)
        {
            DetailSurface.Opacity = 0;
            DetailSurface.Visibility = Visibility.Hidden;
            return;
        }

        _cardTransitionVersion++;
        var offset = _settings.Edge == DockEdge.Right ? 8d : -8d;
        var generation = _selectedLine;
        Animate(DetailSurface, OpacityProperty, 0, 170, () =>
        {
            if (!_cardPinned && generation == _selectedLine && DetailSurface.Opacity <= 0.01)
            {
                DetailSurface.Visibility = Visibility.Hidden;
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
            LineList.Opacity = 1;
            LineList.IsHitTestVisible = true;
            LineListScale.ScaleX = 1;
            LineListScale.ScaleY = 1;
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
        LineList.IsHitTestVisible = true;
        PlusHandle.IsHitTestVisible = true;
        DragHandle.IsHitTestVisible = true;
        Animate(LineList, OpacityProperty, 1, 160);
        Animate(PlusHandle, OpacityProperty, 1, 160);
        Animate(DragHandle, OpacityProperty, 1, 160);
        Animate(PipList, OpacityProperty, 0, 120);
        Animate(LineListScale, ScaleTransform.ScaleXProperty, 1, 190);
        Animate(LineListScale, ScaleTransform.ScaleYProperty, 1, 190);
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
            LineList.Opacity = 0;
            LineList.IsHitTestVisible = false;
            LineListScale.ScaleX = 0.94;
            LineListScale.ScaleY = 0.94;
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

        LineList.IsHitTestVisible = false;
        PlusHandle.IsHitTestVisible = false;
        DragHandle.IsHitTestVisible = false;
        Animate(LineList, OpacityProperty, 0, 90);
        Animate(PlusHandle, OpacityProperty, 0, 90);
        Animate(DragHandle, OpacityProperty, 0, 90);
        Animate(PipList, OpacityProperty, 1, 160);
        Animate(LineListScale, ScaleTransform.ScaleXProperty, 0.94, 140);
        Animate(LineListScale, ScaleTransform.ScaleYProperty, 0.94, 140);
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
                // Stay where the pointer released.
            }
        }
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
        // Original 6-row picker chrome plus the interval row: padding 8+8, three 28px rows, divider 2+1+6, six 36px rows.
        const double height = 8 + 28 + 28 + 28 + 2 + 1 + 6 + 6 * 36 + 8;
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
        ShrinkToRail(keepCurrentTop: false);
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

    private void IntervalItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _settings.IntervalSeconds = SettingsStore.NextInterval(_settings.IntervalSeconds);
        IntervalLabel = $"探测间隔  {_settings.IntervalSeconds} 秒";
        _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.IntervalSeconds);
        SettingsStore.Save(_settings);
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
        _settings.EnabledTargetIds = ProbeCatalog.Normalize(CatalogItems.Where(entry => entry.IsEnabled).Select(entry => entry.Id)).ToList();
        SettingsStore.Save(_settings);
        SyncLines();
        _ = RefreshAsync();
        e.Handled = true;
    }

    private void SyncLines()
    {
        var enabled = ProbeCatalog.Normalize(_settings.EnabledTargetIds);
        for (var index = Lines.Count - 1; index >= 0; index--)
        {
            if (!enabled.Contains(Lines[index].Id, StringComparer.OrdinalIgnoreCase))
            {
                if (_selectedLine == Lines[index])
                {
                    _selectedLine = null;
                    HideDetail(true);
                }

                Lines.RemoveAt(index);
            }
        }

        foreach (var target in ProbeCatalog.Selected(enabled))
        {
            if (Lines.Any(line => line.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var rank = enabled.ToList().FindIndex(id => id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            var insertAt = Lines.Count(line => enabled.ToList().FindIndex(id => id.Equals(line.Id, StringComparison.OrdinalIgnoreCase)) < rank);
            Lines.Insert(Math.Clamp(insertAt, 0, Lines.Count), new LineViewModel(target));
        }

        LayoutRailChrome();
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
        var snapshot = LineScoring.Classify(_lastDraft, _history.Snapshot());
        var window = _history.Snapshot();
        foreach (var line in Lines)
        {
            line.Apply(snapshot, window);
        }

        if (_selectedLine is not null && DetailSurface.Visibility == Visibility.Visible)
        {
            ConfigureLineDetail(_selectedLine);
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

    private void FitWindowToPicker(double pickerHeight) => FitWindowToCard(pickerHeight);

    private void FitWindowToCard(double cardHeight)
    {
        var needed = Math.Max(RailGeometry.HeightFor(LineCount), cardHeight + 16);
        if (Math.Abs(Height - needed) < 0.5)
        {
            return;
        }

        if (!_overlayGrew)
        {
            _topBeforeOverlay = Top;
            _overlayGrew = true;
        }

        Height = needed;
        Stage.Height = needed;
        if (_lastWorkArea.Height > 0)
        {
            Top = ClampTop(_lastWorkArea, Top, Height);
        }
    }

    private void ShrinkToRail(bool keepCurrentTop)
    {
        var railHeight = RailGeometry.HeightFor(LineCount);
        if (Math.Abs(Height - railHeight) < 0.5)
        {
            _overlayGrew = false;
            return;
        }

        var top = keepCurrentTop || !_overlayGrew ? Top : _topBeforeOverlay;
        LayoutRailChrome();
        if (_lastWorkArea.Height > 0)
        {
            Top = ClampTop(_lastWorkArea, top, Height);
        }

        _overlayGrew = false;
        PersistPosition();
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

    private void LayoutRailChrome()
    {
        var height = RailGeometry.HeightFor(LineCount);
        if (_pickerOpen)
        {
            height = Math.Max(height, PickerSurface.Height + 16);
        }

        if (_detailNeed > 1)
        {
            height = Math.Max(height, _detailNeed + 16);
        }

        var railHeight = RailGeometry.HeightFor(LineCount);
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
        Canvas.SetTop(LineList, RailGeometry.ListTop);
        LineList.Height = RailGeometry.ListHeight(LineCount);
        Canvas.SetTop(PipList, RailGeometry.ListTop);
        PipList.Height = RailGeometry.ListHeight(LineCount);
        Canvas.SetLeft(DragHandle, 9);
        Canvas.SetTop(DragHandle, RailGeometry.GearTop(LineCount) + 4);
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

    private void EnsureFloatingChrome()
    {
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

        Left += current - desired;
        Canvas.SetLeft(Rail, desired);
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed record DragSample(System.Windows.Point Point, long Timestamp);
}
