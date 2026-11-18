using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Client.Navigation;

/// <summary>
/// Opens screens (SHELL-13): each visit creates exactly one view model from DI, hands it the parameter, publishes it as
/// <see cref="Current"/> (the view's DataContext is bound to it, so it is set once per visit), disposes the previous one and
/// calls <see cref="ViewModelBase.LoadAsync"/> exactly once.
/// </summary>
public interface INavigationService
{
    ViewModelBase? Current { get; }

    event EventHandler? CurrentChanged;

    /// <summary>Navigates to the view model type. Returns false when the user chose to stay (unsaved changes) or navigation was refused.</summary>
    Task<bool> NavigateTo<TViewModel>(object? parameter = null)
        where TViewModel : ViewModelBase;

    Task<bool> NavigateTo(Type viewModelType, object? parameter = null);

    /// <summary>True when the current screen has unsaved changes.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>Asks "Discard changes?" when the current screen has unsaved changes. True means go ahead (nothing to lose, or the user chose to discard); false means stay.</summary>
    Task<bool> ConfirmDiscardAsync();

    /// <summary>Loads the current screen again (after a demo reset, for example) without creating a new view model.</summary>
    Task ReloadCurrentAsync();

    /// <summary>Disposes the current view model and shows nothing; used when the main window closes.</summary>
    void Reset();
}

public sealed class NavigationService(IServiceProvider services, IDialogService dialogs, IServerStatus? serverStatus = null, TimeProvider? clock = null) : INavigationService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private ViewModelBase? _current;

    public ViewModelBase? Current => _current;

    public event EventHandler? CurrentChanged;

    public Task<bool> NavigateTo<TViewModel>(object? parameter = null)
        where TViewModel : ViewModelBase => NavigateTo(typeof(TViewModel), parameter);

    public async Task<bool> NavigateTo(Type viewModelType, object? parameter = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return false;
        }

        var next = (ViewModelBase)ActivatorUtilities.GetServiceOrCreateInstance(services, viewModelType);
        next.Attach(serverStatus, _clock);
        if (parameter is not null && next is INavigationParameterReceiver receiver)
        {
            receiver.ReceiveParameter(parameter);
        }

        var previous = _current;
        _current = next;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        previous?.Dispose();

        await next.LoadAsync().ConfigureAwait(true);
        return true;
    }

    public bool HasUnsavedChanges => _current is IDirtyTracking { HasUnsavedChanges: true };

    public async Task<bool> ConfirmDiscardAsync() =>
        !HasUnsavedChanges
        || await dialogs.ConfirmAsync("Discard changes?", "You have unsaved changes on this screen. If you leave now they will be lost.", "Discard changes").ConfigureAwait(true);

    public Task ReloadCurrentAsync() => _current?.LoadAsync() ?? Task.CompletedTask;

    public void Reset()
    {
        var previous = _current;
        _current = null;
        previous?.Dispose();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }
}
