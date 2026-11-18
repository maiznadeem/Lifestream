namespace Lifestream.Client.Common;

/// <summary>Implemented by form view models. Navigating away while <see cref="HasUnsavedChanges"/> asks "Discard changes?" (SHELL-24).</summary>
public interface IDirtyTracking
{
    bool HasUnsavedChanges { get; }
}

/// <summary>Implemented by view models that want the navigation parameter before they load.</summary>
public interface INavigationParameterReceiver
{
    void ReceiveParameter(object parameter);
}
