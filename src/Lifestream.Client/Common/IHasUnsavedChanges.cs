namespace Lifestream.Client.Common;

/// <summary>
/// The one dirty-tracking contract (SHELL-24). Every editor and every form dialog that has inputs implements it, and every way out of it
/// (Esc, Cancel, the window's X, navigating away, Add or Ctrl+N, sign-out, closing the window) asks "Discard changes?" while
/// <see cref="HasUnsavedChanges"/> is true.
/// </summary>
public interface IHasUnsavedChanges
{
    bool HasUnsavedChanges { get; }
}

/// <summary>Implemented by view models that want the navigation parameter before they load.</summary>
public interface INavigationParameterReceiver
{
    void ReceiveParameter(object parameter);
}

/// <summary>
/// Marks a form dialog that has no inputs (a viewer, a confirmation), so it has nothing to lose and reports no unsaved changes. The inventory test
/// requires every form view model to either have a dirty edit or carry this attribute with its reason.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NoUserInputAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
