namespace TwinDock.Providers;

internal static class ProviderParsing
{
    public static string WindowLabel(TimeSpan? duration, string fallback)
    {
        if (duration is null)
        {
            return fallback;
        }

        return duration.Value.TotalHours switch
        {
            <= 6.5 => "5 小时限额",
            <= 24.5 => "每日限额",
            <= 24 * 8 => "周限额",
            _ => "订阅周期"
        };
    }
}
