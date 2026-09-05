using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;

namespace LineDock.Controls;

public sealed class StatusRing : FrameworkElement
{
    private static readonly SolidColorBrush WellBrush = CreateFrozenBrush(12, 13, 14);
    private static readonly SolidColorBrush TrackBrush = CreateFrozenBrush(40, 42, 45);
    private static readonly SolidColorBrush UnavailableIconBrush = CreateFrozenBrush(112, 114, 119);
    private static readonly Pen TrackPen = CreateFrozenPen(TrackBrush, 3.2);

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(StatusRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrandProperty = DependencyProperty.Register(
        nameof(Brand),
        typeof(string),
        typeof(StatusRing),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(
        nameof(IsAvailable),
        typeof(bool),
        typeof(StatusRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

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

        if (IsAvailable)
        {
            var accentPen = new Pen(Accent ?? Brushes.White, stroke)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (accentPen.CanFreeze)
            {
                accentPen.Freeze();
            }

            drawingContext.DrawEllipse(null, accentPen, center, radius, radius);
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
}
