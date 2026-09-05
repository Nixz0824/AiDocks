using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace LineDock.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string Identity = "B7C41E90-2A18-4D6F-9E55-6F1C8A0D4B21";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly Action _requestShutdown;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;
    private bool _ownsMutex;
    private int _shutdownRequested;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName, Action requestShutdown)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _requestShutdown = requestShutdown;
        _ownsMutex = true;
        _serverTask = ListenAsync(_cancellation.Token);
    }

    public static SingleInstanceCoordinator? AcquireLatest(bool qaInstance, Action requestShutdown)
    {
        var scope = qaInstance ? "QA" : "Main";
        var mutexName = qaInstance
            ? $@"Local\LineDock.QA.{Identity}"
            : $@"Local\LineDock.{Identity}";
        var pipeName = $"LineDock.{scope}.{Identity}";
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, mutexName);
        }
        catch
        {
            return null;
        }

        if (!TryAcquire(mutex, TimeSpan.Zero))
        {
            TryRequestShutdown(pipeName);
            if (!TryAcquire(mutex, TimeSpan.FromSeconds(4)))
            {
                if (!qaInstance)
                {
                    TerminateLegacyProcesses();
                }
                if (!TryAcquire(mutex, TimeSpan.FromSeconds(5)))
                {
                    mutex.Dispose();
                    return null;
                }
            }
        }

        return new SingleInstanceCoordinator(mutex, pipeName, requestShutdown);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Cancellation is expected during shutdown.
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Ownership was already abandoned during process teardown.
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
        _cancellation.Dispose();
    }

    internal static bool IsLineDockProduct(string path)
    {
        try
        {
            if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.ProductName, "LineDock", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, "shutdown", StringComparison.Ordinal) &&
                    Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
                {
                    _requestShutdown();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
        }
    }

    private static bool TryAcquire(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static void TryRequestShutdown(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect(700);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("shutdown");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // Older versions do not host the control pipe.
        }
    }

    private static void TerminateLegacyProcesses()
    {
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId ||
                        !process.ProcessName.StartsWith("LineDock", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !IsLineDockProduct(path))
                    {
                        continue;
                    }

                    process.Kill(true);
                    process.WaitForExit(4000);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Never broaden the target when a candidate cannot be verified.
                }
            }
        }
    }
}
