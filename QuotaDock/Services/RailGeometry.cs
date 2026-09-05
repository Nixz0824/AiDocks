using System.Globalization;

namespace QuotaDock.Services;

internal static class RailGeometry
{
    public const double Width = 50;
    public const double ItemPitch = 72;
    public const double EndControl = 40;
    public const double EdgePad = 24;
    public const double EdgeRadius = 20;
    public const double InnerRadius = 24;
    public const double CardColumn = 194;

    public static double HeightFor(int providerCount)
    {
        var count = Math.Max(0, providerCount);
        return EdgePad + EndControl + count * ItemPitch + EndControl + EdgePad;
    }

    public static double PlusTop => EdgePad;
    public static double ListTop => EdgePad + EndControl;
    public static double ListHeight(int providerCount) => Math.Max(0, providerCount) * ItemPitch;
    public static double GearTop(int providerCount) => EdgePad + EndControl + ListHeight(providerCount);

    public static double RingCenterY(int index) => ListTop + index * ItemPitch + 24;

    public static string BuildPath(double height, double morph = 0)
    {
        var n = CultureInfo.InvariantCulture;
        var w = Width;
        var h = height;
        var t = Math.Clamp(morph, 0, 1);
        var floating = t >= 0.5;
        var blend = floating ? (t - 0.5) * 2 : 1 - t * 2;
        var rScreen = Math.Max(0.8, (floating ? InnerRadius : EdgeRadius) * blend);
        var sweep = floating ? 1 : 0;
        var rInner = InnerRadius;
        var rightTopY = floating ? rScreen : 0;
        var rightBotY = floating ? h - rScreen : h;
        var afterBrX = w - rScreen;
        var afterBrY = floating ? h : h - rScreen;
        var leftBotY = afterBrY - rInner;
        var leftTopY = floating ? rInner : rScreen + rInner;
        var afterTlY = floating ? 0 : rScreen;
        return string.Format(
            n,
            "M {0},{1} L {0},{2} A {3},{3} 0 0 {4} {5},{6} L {7},{6} A {7},{7} 0 0 1 0,{8} L 0,{9} A {7},{7} 0 0 1 {7},{10} L {5},{10} A {3},{3} 0 0 {4} {0},{1} Z",
            w, rightTopY, rightBotY, rScreen, sweep, afterBrX, afterBrY, rInner, leftBotY, leftTopY, afterTlY);
    }
}
