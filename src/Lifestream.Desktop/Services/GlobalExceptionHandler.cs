using System.Windows.Threading;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Services;

/// <summary>
/// Installs the three global handlers (SHELL-01): the dispatcher (UI thread), the AppDomain and unobserved task exceptions.
/// Each logs the error and shows the friendly dialog through <see cref="IErrorReporter"/>; the dispatcher and task handlers mark the
/// exception handled so the process keeps running. (.NET cannot cancel a crash on a non-UI thread, so the AppDomain handler
/// reports and the code never lets a command's exception reach it: <see cref="AsyncRelayCommand"/> captures them all.)
/// </summary>
public sealed class GlobalExceptionHandler : IDisposable
{
    private readonly IErrorReporter _reporter;
    private readonly Dispatcher _dispatcher;
    private bool _installed;

    public GlobalExceptionHandler(IErrorReporter reporter, Dispatcher dispatcher)
    {
        _reporter = reporter;
        _dispatcher = dispatcher;
    }

    /// <summary>The number of exceptions reported so far.</summary>
    public int ReportedCount { get; private set; }

    public void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _dispatcher.UnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        AsyncRelayCommand.DefaultErrorHandler = ex => Report(ex, "command");
    }

    public void Dispose()
    {
        if (!_installed)
        {
            return;
        }

        _installed = false;
        _dispatcher.UnhandledException -= OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report(e.Exception, "dispatcher");
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Report(ex, "appdomain");
        }
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Report(e.Exception, "task");
    }

    private void Report(Exception exception, string source)
    {
        ReportedCount++;
        try
        {
            _reporter.Report(exception, source);
        }
        catch (Exception reportFailure) when (reportFailure is not OutOfMemoryException)
        {
            // Reporting must never throw back into the handler that called it.
            System.Diagnostics.Debug.WriteLine(reportFailure);
        }
    }
}
