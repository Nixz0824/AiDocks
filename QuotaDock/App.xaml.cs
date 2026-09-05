using System.Windows;
using System.Windows.Threading;
using QuotaDock.Services;

namespace QuotaDock;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(SelfTest.Run());
            return;
        }

        if (e.Args.Contains("--probe-quota", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(Task.Run(QuotaProbe.Run).GetAwaiter().GetResult());
            return;
        }

        var qaInstance = e.Args.Contains("--qa-instance", StringComparer.OrdinalIgnoreCase);
        _singleInstance = SingleInstanceCoordinator.AcquireLatest(
            qaInstance,
            () => Dispatcher.BeginInvoke(() => Shutdown(0)));
        if (_singleInstance is null)
        {
            Shutdown(0);
            return;
        }

        var demoMode = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var motionPreview = e.Args.Contains("--motion-preview", StringComparer.OrdinalIgnoreCase);
        var openMenu = e.Args.Contains("--open-menu", StringComparer.OrdinalIgnoreCase);
        var dragReproPath = e.Args.FirstOrDefault(argument => argument.StartsWith("--drag-repro=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        var pickerReproPath = e.Args.FirstOrDefault(argument => argument.StartsWith("--picker-repro=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        var initialProvider = e.Args.FirstOrDefault(argument => argument.StartsWith("--open=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        var settings = SettingsStore.Load();
        if (e.Args.Contains("--edge=left", StringComparer.OrdinalIgnoreCase))
        {
            settings.Edge = DockEdge.Left;
        }
        else if (e.Args.Contains("--edge=right", StringComparer.OrdinalIgnoreCase))
        {
            settings.Edge = DockEdge.Right;
        }

        var window = new MainWindow(settings, demoMode, initialProvider, motionPreview, openMenu || pickerReproPath is not null, dragReproPath, pickerReproPath);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _singleInstance?.Dispose();

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        if (IsRecoverableUiException(args.Exception))
        {
            args.Handled = true;
            return;
        }

        CrashLog.Write(args.Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        CrashLog.Write(args.Exception);
        args.SetObserved();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            CrashLog.Write(exception);
        }
    }

    internal static bool IsRecoverableUiException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not InvalidOperationException)
            {
                continue;
            }

            var message = current.Message;
            if (message.Contains("冻结", StringComparison.Ordinal) ||
                message.Contains("frozen", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("PresentationSource", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Freezable", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
