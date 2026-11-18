using System.Collections;
using System.ComponentModel;
using Lifestream.Client.Common;

namespace Lifestream.Client.Validation;

/// <summary>
/// Base of every form view model (SHELL-32, SHELL-24). Each field has at most one message; the view shows it with an icon
/// under the field on blur and on submit, announces it, and moves focus to the first invalid field when
/// <see cref="FocusRequested"/> fires. <see cref="HasUnsavedChanges"/> drives the "Discard changes?" prompt.
/// </summary>
public abstract class FormViewModelBase : ViewModelBase, INotifyDataErrorInfo, IDirtyTracking
{
    private readonly Dictionary<string, string> _errors = [];
    private readonly List<string> _fieldOrder = [];

    /// <summary>Raised with the field name when the view should focus that field.</summary>
    public event EventHandler<string>? FocusRequested;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    /// <summary>One line such as "2 fields need attention" for the summary above the form.</summary>
    public string? ErrorSummary => _errors.Count == 0 ? null : _errors.Count == 1 ? "1 field needs attention." : $"{_errors.Count} fields need attention.";

    public virtual bool HasUnsavedChanges => false;

    /// <summary>The field names in visual (tab) order, so focus goes to the first invalid one.</summary>
    protected void DeclareFields(params string[] fieldsInVisualOrder)
    {
        _fieldOrder.Clear();
        _fieldOrder.AddRange(fieldsInVisualOrder);
    }

    /// <summary>Returns the message for a field, or null when it is valid. Called on blur and on submit.</summary>
    protected abstract string? ValidateField(string field);

    public string? GetError(string field) => _errors.GetValueOrDefault(field);

    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && _errors.TryGetValue(propertyName, out var message) ? new[] { message } : Array.Empty<string>();

    /// <summary>Validates one field (call from the field's LostFocus).</summary>
    public bool ValidateOnBlur(string field)
    {
        var message = ValidateField(field);
        SetError(field, message);
        return message is null;
    }

    /// <summary>Validates every declared field; on failure asks the view to focus the first invalid one in visual order.</summary>
    public bool ValidateAll()
    {
        foreach (var field in _fieldOrder)
        {
            SetError(field, ValidateField(field));
        }

        var first = _fieldOrder.FirstOrDefault(f => _errors.ContainsKey(f));
        if (first is not null)
        {
            FocusRequested?.Invoke(this, first);
            return false;
        }

        return true;
    }

    protected void SetError(string field, string? message)
    {
        var changed = message is null ? _errors.Remove(field) : !_errors.TryGetValue(field, out var old) || old != message;
        if (message is not null)
        {
            _errors[field] = message;
        }

        if (changed)
        {
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(field));
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(ErrorSummary));
        }
    }

    /// <summary>Clears a server-side error (for example a 409 message) the moment the user edits the field.</summary>
    protected void ClearError(string field) => SetError(field, null);
}
