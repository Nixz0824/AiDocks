using System.IO;
using System.Text.Json;
using QuotaDock.Models;
using QuotaDock.Providers;

namespace QuotaDock.Services;

internal static class QuotaProbe
{
    public static int Run()
    {
        try
        {
            using var service = new QuotaService();
            var ids = ProviderCatalog.All.Select(item => item.Id).ToArray();
            var snapshots = service.RefreshAsync(false, ids, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            var rows = snapshots.Select(snapshot =>
            {
                var headline = QuotaPresentation.HeadlineWindow(snapshot.Windows);
                var remaining = headline is null ? (double?)null : QuotaPresentation.Remaining(headline.UsedPercent);
                return new
                {
                    snapshot.ProviderId,
                    snapshot.State,
                    snapshot.StatusMessage,
                    live = ProviderCatalog.All.First(item => item.Id == snapshot.ProviderId).HasLiveQuota,
                    headlineLabel = headline?.Label,
                    headlineUsed = headline?.UsedPercent,
                    headlineRemaining = remaining,
                    windows = snapshot.Windows.Select(window => new
                    {
                        window.Label,
                        window.UsedPercent,
                        remaining = QuotaPresentation.Remaining(window.UsedPercent),
                        reset = window.ResetsAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    })
                };
            });
            var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
            var path = Path.Combine(Path.GetTempPath(), "quotadock-probe.json");
            File.WriteAllText(path, json);
            Console.WriteLine(path);
            Console.WriteLine(json);
            return snapshots.Any(item => item.State is ProviderState.Ready or ProviderState.Stale) ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 2;
        }
    }
}
