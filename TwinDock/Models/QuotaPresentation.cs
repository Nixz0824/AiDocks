using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace TwinDock.Models;

internal static class QuotaPresentation
{
    public static double Remaining(double usedPercent) => Math.Clamp(100 - usedPercent, 0, 100);

    public static QuotaWindow? HeadlineWindow(IReadOnlyList<QuotaWindow> windows)
    {
        QuotaWindow? month = null;
        QuotaWindow? week = null;
        foreach (var window in windows)
        {
            switch (Horizon(window))
            {
                case QuotaHorizon.Month:
                    month = Tighter(month, window);
                    break;
                case QuotaHorizon.Week:
                    week = Tighter(week, window);
                    break;
            }
        }

        return month ?? week ?? windows.MaxBy(window => window.UsedPercent);
    }

    private static QuotaWindow Tighter(QuotaWindow? current, QuotaWindow candidate) =>
        current is null || candidate.UsedPercent > current.UsedPercent ? candidate : current;

    public static Brush BrushForRemaining(double remainingPercent)
    {
        var brush = new SolidColorBrush(ColorForRemaining(remainingPercent));
        brush.Freeze();
        return brush;
    }

    public static Color ColorForRemaining(double remainingPercent)
    {
        var remaining = Math.Clamp(remainingPercent, 0, 100);
        if (remaining >= 50)
        {
            return Lerp(Color.FromRgb(0xFF, 0xC1, 0x4A), Color.FromRgb(0x20, 0xE3, 0xA2), (remaining - 50) / 50);
        }

        return Lerp(Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0xC1, 0x4A), remaining / 50);
    }

    private static QuotaHorizon Horizon(QuotaWindow window)
    {
        if (window.Label.Contains("月", StringComparison.Ordinal) ||
            window.Label.Contains("订阅", StringComparison.Ordinal))
        {
            return QuotaHorizon.Month;
        }

        if (window.Label.Contains("周", StringComparison.Ordinal))
        {
            return QuotaHorizon.Week;
        }

        if (window.Duration is { } duration)
        {
            if (duration.TotalDays >= 20)
            {
                return QuotaHorizon.Month;
            }

            if (duration.TotalDays >= 2)
            {
                return QuotaHorizon.Week;
            }
        }

        return QuotaHorizon.Short;
    }

    private static Color Lerp(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private enum QuotaHorizon
    {
        Short,
        Week,
        Month
    }
}
