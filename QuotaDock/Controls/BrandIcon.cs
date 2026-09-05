using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace QuotaDock.Controls;

public sealed class BrandIcon : FrameworkElement
{
    // Official marks, viewBox 0 0 24 24. OpenAI/xAI as before; others from Simple Icons / brand geometry.
    // opencode: official square mark from packages/console/app/src/asset/brand/opencode-logo-light-square.svg
    // (sst/opencode, now anomalyco/opencode), reduced to mono EvenOdd donut for the dark rail.
    private static readonly Dictionary<string, string> Marks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"] = "F1 M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z",
        ["chatgpt"] = "F1 M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0 .3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z",
        ["grok"] = "F1 M6.469,8.776 L16.512,23 H12.048 L2.005,8.776 H6.47 Z M6.465,16.676 L8.698,19.84 L6.467,23 H2 L6.465,16.676 Z M22,2.582 V23 H18.341 V7.764 Z M22,1 L12.048,15.095 L9.815,11.932 L17.533,1 H22 Z",
        ["claude"] = "F1 M17.3041,3.541 H13.6323 L20.3283,20.459 H24 Z M6.6959,3.541 L0,20.459 H3.7442 L5.1135,16.9063 H12.1187 L13.488,20.4591 H17.2322 L10.5363,3.5409 Z M6.3247,13.7642 L8.6161,7.8186 10.9075,13.7642 Z",
        ["cursor"] = "F1 M11.503,0.131 L1.891,5.678 A0.84,0.84 0 0 0 1.471,6.404 L1.471,17.592 A0.84,0.84 0 0 0 1.891,18.316 L11.5,23.866 A1,1 0 0 0 12.498,23.866 L22.108,18.316 A0.84,0.84 0 0 0 22.528,17.592 L22.528,6.404 A0.84,0.84 0 0 0 22.108,5.678 L12.497,0.131 A1.01,1.01 0 0 0 11.501,0.131 Z M2.657,6.338 L21.207,6.338 C21.47,6.338 21.637,6.625 21.504,6.853 L12.23,22.918 C12.168,23.025 12.001,22.982 12.001,22.858 L12.001,12.335 A0.59,0.59 0 0 0 11.706,11.825 L2.596,6.568 C2.487,6.505 2.532,6.338 2.657,6.338 Z",
        ["gemini"] = "F1 M12,0 L14.4,9.6 24,12 14.4,14.4 12,24 9.6,14.4 0,12 9.6,9.6 Z",
        ["opencode"] = "F0 M20,22 H4 V2 H20 V22 Z M16,18 H8 V6 H16 V18 Z"
    };

    public static readonly DependencyProperty BrandProperty = DependencyProperty.Register(
        nameof(Brand),
        typeof(string),
        typeof(BrandIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground),
        typeof(Brush),
        typeof(BrandIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Brand
    {
        get => (string)GetValue(BrandProperty);
        set => SetValue(BrandProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsNaN(availableSize.Width) || double.IsInfinity(availableSize.Width) ? 16 : availableSize.Width;
        var height = double.IsNaN(availableSize.Height) || double.IsInfinity(availableSize.Height) ? 16 : availableSize.Height;
        var side = Math.Min(width, height);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        Draw(drawingContext, Brand, new Point(ActualWidth / 2, ActualHeight / 2), side * 0.92, Foreground);
    }

    private static readonly Dictionary<string, PathGeometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static void Draw(DrawingContext drawingContext, string? brand, Point center, double size, Brush brush)
    {
        var key = Marks.ContainsKey(brand ?? string.Empty) ? brand! : "codex";
        if (!Cache.TryGetValue(key, out var geometry))
        {
            try
            {
                geometry = PathGeometry.CreateFromGeometry(Geometry.Parse(Marks[key]));
                geometry.Freeze();
                Cache[key] = geometry;
            }
            catch (FormatException)
            {
                return;
            }
        }

        var bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var scale = size / Math.Max(bounds.Width, bounds.Height);
        var matrix = new Matrix();
        matrix.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
        matrix.Scale(scale, scale);
        matrix.Translate(center.X, center.Y);
        drawingContext.PushTransform(new MatrixTransform(matrix));
        drawingContext.DrawGeometry(brush, null, geometry);
        drawingContext.Pop();
    }
}
