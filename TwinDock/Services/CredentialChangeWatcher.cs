using System.IO;

namespace TwinDock.Services;

internal sealed class CredentialChangeWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];

    public event EventHandler? Changed;

    public void Start()
    {
        if (_watchers.Count > 0)
        {
            return;
        }

        AddWatcher(Environment.GetEnvironmentVariable("CODEX_HOME"), ".codex", "auth.json");
        AddWatcher(Environment.GetEnvironmentVariable("GROK_HOME"), ".grok", "auth.json");
        AddWatcher(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), ".claude", ".credentials.json");
        AddWatcher(null, ".gemini", "oauth_creds.json");
        AddOpenCodeWatcher();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void AddWatcher(string? configuredHome, string defaultFolder, string filter)
    {
        var directory = !string.IsNullOrWhiteSpace(configuredHome)
            ? configuredHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), defaultFolder);
        AddDirectoryWatcher(directory, filter);
    }

    private void AddOpenCodeWatcher()
    {
        // opencode 主路径跨平台都是 ~/.local/share/opencode/auth.json（含 Windows）。
        AddDirectoryWatcher(Providers.OpenCodeQuotaProvider.AuthDirectory(), "auth.json");
    }

    private void AddDirectoryWatcher(string? directory, string filter)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory, filter)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            _watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The regular refresh timer remains the fallback.
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    private void OnRenamed(object sender, RenamedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
