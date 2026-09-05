using System.Text.Json;
using System.IO;
using QuotaDock.Models;
using QuotaDock.Providers;

namespace QuotaDock.Services;

internal sealed class QuotaCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _path;
    private readonly object _gate = new();

    public QuotaCacheStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuotaDock",
            "usage-cache.json");
    }

    public IReadOnlyList<QuotaSnapshot> InitialSnapshots(IEnumerable<IQuotaProvider> providers)
    {
        var cache = Load();
        return providers.Select(provider =>
        {
            if (cache.TryGetValue(provider.Id, out var snapshot) && IsUsable(snapshot))
            {
                return AsStale(snapshot, "等待后台刷新");
            }

            return new QuotaSnapshot(provider.Id, provider.DisplayName, provider.Glyph, provider.AccentHex, [], DateTimeOffset.Now, ProviderState.Loading);
        }).ToArray();
    }

    public IReadOnlyList<QuotaSnapshot> MergeAndSave(IReadOnlyList<QuotaSnapshot> fresh)
    {
        lock (_gate)
        {
            var cache = LoadUnlocked();
            var changed = false;
            var merged = fresh.Select(snapshot => MergeOne(snapshot, cache, ref changed)).ToList();
            if (changed)
            {
                SaveUnlocked(cache);
            }

            return merged;
        }
    }

    public QuotaSnapshot MergeAndSaveOne(QuotaSnapshot fresh)
    {
        lock (_gate)
        {
            var cache = LoadUnlocked();
            var changed = false;
            var merged = MergeOne(fresh, cache, ref changed);
            if (changed)
            {
                SaveUnlocked(cache);
            }

            return merged;
        }
    }

    private static QuotaSnapshot MergeOne(QuotaSnapshot snapshot, Dictionary<string, QuotaSnapshot> cache, ref bool changed)
    {
        if (snapshot.State == ProviderState.Ready && snapshot.Windows.Count > 0)
        {
            cache[snapshot.ProviderId] = snapshot;
            changed = true;
            return snapshot;
        }

        if (cache.TryGetValue(snapshot.ProviderId, out var cached) && IsUsable(cached))
        {
            return AsStale(cached, snapshot.StatusMessage ?? StateMessage(snapshot.State));
        }

        return snapshot;
    }

    private Dictionary<string, QuotaSnapshot> Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    private Dictionary<string, QuotaSnapshot> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
            }

            return JsonSerializer.Deserialize<Dictionary<string, QuotaSnapshot>>(File.ReadAllText(_path), JsonOptions)
                   ?? new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnlocked(IReadOnlyDictionary<string, QuotaSnapshot> snapshots)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshots, JsonOptions));
            File.Move(temporary, _path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The live result still reaches the UI when persistence is unavailable.
        }
    }

    private static bool IsUsable(QuotaSnapshot snapshot)
    {
        if (snapshot.Windows.Count == 0 || DateTimeOffset.Now - snapshot.UpdatedAt > TimeSpan.FromHours(24))
        {
            return false;
        }

        return snapshot.Windows.Any(window => window.ResetsAt is null || window.ResetsAt > DateTimeOffset.Now);
    }

    private static QuotaSnapshot AsStale(QuotaSnapshot snapshot, string reason)
    {
        var age = DateTimeOffset.Now - snapshot.UpdatedAt;
        var ageText = age.TotalMinutes < 2 ? "刚刚" : age.TotalHours < 1 ? $"{Math.Max(2, (int)age.TotalMinutes)} 分钟前" : $"{Math.Max(1, (int)age.TotalHours)} 小时前";
        return snapshot with
        {
            State = ProviderState.Stale,
            StatusMessage = $"上次更新于 {ageText} · {reason}"
        };
    }

    private static string StateMessage(ProviderState state) => state switch
    {
        ProviderState.AuthenticationRequired => "登录需要续期",
        ProviderState.MissingCredentials => "未检测到登录",
        _ => "后台刷新暂不可用"
    };
}
