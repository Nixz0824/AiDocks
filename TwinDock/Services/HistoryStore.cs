using System.IO;
using System.Text.Json;
using TwinDock.Models;

namespace TwinDock.Services;

internal sealed class HistoryStore
{
    private const int Limit = 180;
    private readonly object _gate = new();
    private readonly List<ProbeSnapshot> _items = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string PathName => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TwinDock",
        "history.json");

    public IReadOnlyList<ProbeSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    public void Add(ProbeSnapshot item)
    {
        lock (_gate)
        {
            _items.Add(item);
            var extra = _items.Count - Limit;
            if (extra > 0)
            {
                _items.RemoveRange(0, extra);
            }
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(PathName))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<ProbeSnapshot>>(File.ReadAllText(PathName), JsonOptions);
            if (loaded is null || loaded.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                _items.Clear();
                _items.AddRange(loaded.TakeLast(Limit));
            }
        }
        catch
        {
            // History is a convenience, not required to start.
        }
    }

    public void Save()
    {
        try
        {
            List<ProbeSnapshot> copy;
            lock (_gate)
            {
                copy = _items.ToList();
            }

            var directory = System.IO.Path.GetDirectoryName(PathName)!;
            Directory.CreateDirectory(directory);
            var temporary = PathName + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(copy, JsonOptions));
            File.Move(temporary, PathName, true);
        }
        catch
        {
            // Persistence is best-effort.
        }
    }
}
