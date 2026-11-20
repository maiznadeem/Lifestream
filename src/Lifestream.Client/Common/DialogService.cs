using Microsoft.Extensions.Logging;

namespace Lifestream.Client.Common;

public interface IDialogService
{
    /// <summary>
    /// Asks the user to confirm a destructive action. The title and body name the record; focus starts on the keep button; Esc keeps.
    /// The danger button reads <paramref name="dangerVerb"/> ("Cancel appointment") and the other button says what it keeps,
    /// <paramref name="keepVerb"/> ("Keep appointment"), never a bare "Cancel" beside a "Cancel ..." action. Returns true only for the danger button.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string body, string dangerVerb, string keepVerb);

    /// <summary>Shows a friendly error dialog; <paramref name="details"/> sits behind a "Details" control.</summary>
    Task ShowErrorAsync(string title, string message, string? details = null);
}

/// <summary>The view side of <see cref="IDialogService"/>; the Desktop project registers the WPF implementation.</summary>
public interface IDialogPresenter
{
    Task<bool> ConfirmAsync(string title, string body, string dangerVerb, string keepVerb);

    Task ShowErrorAsync(string title, string message, string? details);
}

public sealed class DialogService : IDialogService
{
    public IDialogPresenter? Presenter { get; set; }

    public Task<bool> ConfirmAsync(string title, string body, string dangerVerb, string keepVerb) =>
        Presenter is null ? Task.FromResult(false) : Presenter.ConfirmAsync(title, body, dangerVerb, keepVerb);

    public Task ShowErrorAsync(string title, string message, string? details = null) =>
        Presenter?.ShowErrorAsync(title, message, details) ?? Task.CompletedTask;
}

/// <summary>Logs an unexpected error and shows a friendly dialog (SHELL-01). Used by the global handlers and by every command.</summary>
public interface IErrorReporter
{
    void Report(Exception exception, string source);
}

public sealed partial class ErrorReporter(ILogger<ErrorReporter> logger, IDialogService dialogs) : IErrorReporter
{
    public void Report(Exception exception, string source)
    {
        ArgumentNullException.ThrowIfNull(exception);
        LogUnhandled(logger, source, exception);
        try
        {
            _ = dialogs.ShowErrorAsync(
                "Something went wrong",
                "Lifestream hit an unexpected problem, but your session is safe. You can carry on, or try the action again.",
                $"{exception.GetType().Name}: {exception.Message}");
        }
        catch (Exception dialogFailure) when (dialogFailure is not OutOfMemoryException)
        {
            LogDialogFailed(logger, dialogFailure);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error from {Source}")]
    private static partial void LogUnhandled(ILogger logger, string source, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The error dialog itself failed")]
    private static partial void LogDialogFailed(ILogger logger, Exception exception);
}
