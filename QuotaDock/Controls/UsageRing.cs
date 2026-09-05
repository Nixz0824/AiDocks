using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;

namespace QuotaDock.Controls;

public sealed class UsageRing : FrameworkElement
{
    private static readonly KeySpline StrongEaseOut = new(0.23, 1, 0.32, 1);
    private static readonly SolidColorBrush WellBrush = CreateFrozenBrush(12, 13, 14);
    private static readonly SolidColorBrush TrackBrush = CreateFrozenBrush(40, 42, 45);
    private static readonly SolidColorBrush UnavailableIconBrush = CreateFrozenBrush(112, 114, 119);
    private static readonly Pen TrackPen = CreateFrozenPen(TrackBrush, 3.2);

    public static readonly DependencyProperty PercentageProperty = DependencyProperty.Register(
        nameof(Percentage),
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(0d, OnPercentageChanged));

    private static readonly DependencyProperty DisplayPercentageProperty = DependencyProperty.Register(
        "DisplayPercentage",
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrandProperty = DependencyProperty.Register(
        nameof(Brand),
        typeof(string),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(
        nameof(IsAvailable),
        typeof(bool),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percentage
    {
        get => (double)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public string Brand
    {
        get => (string)GetValue(BrandProperty);
        set => SetValue(BrandProperty, value);
    }

    public bool IsAvailable
    {
        get => (bool)GetValue(IsAvailableProperty);
        set => SetValue(IsAvailableProperty, value);
    }

    private double DisplayPercentage
    {
        get => (double)GetValue(DisplayPercentageProperty);
        set => SetValue(DisplayPercentageProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(32, 32);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var diameter = Math.Min(ActualWidth, ActualHeight);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var stroke = Math.Max(3.0, diameter * 0.10);
        var radius = Math.Max(2, diameter / 2 - stroke * 1.08);
        var trackPen = stroke.Equals(TrackPen.Thickness) ? TrackPen : CreateFrozenPen(TrackBrush, stroke);
        drawingContext.DrawEllipse(WellBrush, trackPen, center, radius, radius);

        var percentage = Math.Clamp(DisplayPercentage, 0, 100);
        if (IsAvailable && percentage > 0.05)
        {
            var accentPen = new Pen(Accent ?? Brushes.White, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (accentPen.CanFreeze)
            {
                accentPen.Freeze();
            }
            if (percentage >= 99.95)
            {
                drawingContext.DrawEllipse(null, accentPen, center, radius, radius);
            }
            else
            {
                var start = PointOnCircle(center, radius, -90);
                var end = PointOnCircle(center, radius, -90 + percentage * 3.6);
                var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
                figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, percentage > 50, SweepDirection.Clockwise, true));
                var geometry = new PathGeometry([figure]);
                geometry.Freeze();
                drawingContext.DrawGeometry(null, accentPen, geometry);
            }
        }

        var iconBrush = IsAvailable ? Brushes.White : UnavailableIconBrush;
        BrandIcon.Draw(drawingContext, Brand, center, diameter * 0.42, iconBrush);
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static void OnPercentageChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var ring = (UsageRing)dependencyObject;
        var target = Math.Clamp(args.NewValue is double value && double.IsFinite(value) ? value : 0, 0, 100);
        if (!ring.IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            ring.BeginAnimation(DisplayPercentageProperty, null);
            ring.DisplayPercentage = target;
            return;
        }

        var current = ring.DisplayPercentage;
        ring.BeginAnimation(DisplayPercentageProperty, null);
        ring.DisplayPercentage = current;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(260),
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), StrongEaseOut));
        ring.BeginAnimation(DisplayPercentageProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
