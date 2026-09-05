using System.Diagnostics;
using System.IO;

namespace TwinDock.Providers;

internal sealed class GrokCliCredentialRefresher
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null || DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromMinutes(5))
        {
            return false;
        }

        await Gate.WaitAsync(cancellationToken);
        Process? process = null;
        try
        {
            if (DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromMinutes(5))
            {
                return false;
            }

            _lastAttempt = DateTimeOffset.UtcNow;
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "models",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.Environment["NO_COLOR"] = "1";

            if (!process.Start())
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch
                {
                    // The helper already exited.
                }

                process.Dispose();
            }

            Gate.Release();
        }
    }

    private static string? ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("GROK_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var home = Environment.GetEnvironmentVariable("GROK_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
        }

        var canonical = Path.Combine(home, "bin", "grok.exe");
        return File.Exists(canonical) ? canonical : null;
    }
}
