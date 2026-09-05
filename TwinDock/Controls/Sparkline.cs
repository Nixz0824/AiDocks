using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;

namespace TwinDock.Controls;

public sealed class Sparkline : FrameworkElement
{
    private static readonly SolidColorBrush FillBrush = CreateBrush(32, 227, 162, 36);
    private static readonly SolidColorBrush LineBrush = CreateBrush(32, 227, 162, 220);
    private static readonly SolidColorBrush FailBrush = CreateBrush(255, 69, 58, 180);
    private static readonly Pen LinePen = CreatePen(LineBrush, 1.4);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IList<double?>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(LineBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<double?>? Values
    {
        get => (IList<double?>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 144 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 28 : availableSize.Height;
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var values = Values;
        var width = ActualWidth;
        var height = ActualHeight;
        if (values is null || values.Count < 2 || width < 4 || height < 4)
        {
            return;
        }

        var finite = values.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        var max = finite.Length == 0 ? 1 : Math.Max(120, finite.Max());
        var step = width / Math.Max(1, values.Count - 1);
        var points = new List<Point>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var sample = values[index];
            var y = sample is > 0
                ? height - 2 - (sample.Value / max) * (height - 4)
                : height - 2;
            points.Add(new Point(index * step, y));
        }

        var figure = new PathFigure { StartPoint = new Point(points[0].X, height), IsFilled = true, IsClosed = true };
        foreach (var point in points)
        {
            figure.Segments.Add(new LineSegment(point, true));
        }

        figure.Segments.Add(new LineSegment(new Point(points[^1].X, height), false));
        var fill = new PathGeometry([figure]);
        fill.Freeze();
        drawingContext.DrawGeometry(FillBrush, null, fill);

        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (var index = 1; index < points.Count; index++)
            {
                context.LineTo(points[index], true, true);
            }
        }

        line.Freeze();
        var pen = Accent is SolidColorBrush accent
            ? CreatePen(accent, 1.4)
            : LinePen;
        drawingContext.DrawGeometry(null, pen, line);

        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is > 0)
            {
                continue;
            }

            drawingContext.DrawEllipse(FailBrush, null, points[index], 1.6, 1.6);
        }
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b, byte a)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }
}
