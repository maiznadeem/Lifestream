using System.Collections;
using System.ComponentModel;
using Lifestream.Client.Common;

namespace Lifestream.Client.Validation;

/// <summary>
/// Base of every form view model (SHELL-32, SHELL-24). Each field has at most one message; the view shows it with an icon
/// under the field on blur and on submit, announces it, and moves focus to the first invalid field when
/// <see cref="FocusRequested"/> fires. <see cref="HasUnsavedChanges"/> drives the "Discard changes?" prompt.
/// </summary>
public abstract class FormViewModelBase : ViewModelBase, INotifyDataErrorInfo, IHasUnsavedChanges
{
    private readonly Dictionary<string, string> _errors = [];
    private readonly List<string> _fieldOrder = [];

    /// <summary>Raised with the field name when the view should focus that field.</summary>
    public event EventHandler<string>? FocusRequested;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    /// <summary>One line such as "2 fields need attention" for the summary above the form.</summary>
    public string? ErrorSummary => _errors.Count == 0 ? null : _errors.Count == 1 ? "1 field needs attention." : $"{_errors.Count} fields need attention.";

    /// <summary>True while the form holds input that was not saved (the contract of <see cref="IHasUnsavedChanges"/>). Every form says so itself; a form with no inputs says false.</summary>
    public abstract bool HasUnsavedChanges { get; }

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

    private readonly Dictionary<string, string> _inputErrors = [];

    /// <summary>
    /// Called by an input control whose text is not a value at all (the date field holding "31 Feb 2030"): the field's bound value is still the
    /// previous one, so the form must not be saved until the text is put right. The message blocks <see cref="ValidateAll"/> like any other;
    /// null means the text is a value again. With <paramref name="show"/> false (the person is still typing) the message is kept back until
    /// the field is validated (on blur, or by a save), so every way of saving is blocked without a message appearing at each keystroke.
    /// </summary>
    public void SetInputError(string field, string? message, bool show = true)
    {
        ArgumentNullException.ThrowIfNull(field);
        _inputErrors.Remove(field, out var old);
        if (message is not null)
        {
            _inputErrors[field] = message;
            if (show || GetError(field) == old)
            {
                SetError(field, show ? message : null);
            }
        }
        else if (old is not null && GetError(field) == old)
        {
            SetError(field, null);
        }
    }

    /// <summary>The message of a field: what its input control refused, else what the form's own rules say.</summary>
    private string? MessageFor(string field) => _inputErrors.GetValueOrDefault(field) ?? ValidateField(field);

    /// <summary>Validates one field (call from the field's LostFocus).</summary>
    public bool ValidateOnBlur(string field)
    {
        var message = MessageFor(field);
        SetError(field, message);
        return message is null;
    }

    /// <summary>Validates every declared field; on failure asks the view to focus the first invalid one in visual order.</summary>
    public bool ValidateAll()
    {
        foreach (var field in _fieldOrder.Concat(_inputErrors.Keys.Except(_fieldOrder).ToList()))
        {
            SetError(field, MessageFor(field));
        }

        var first = _fieldOrder.FirstOrDefault(f => _errors.ContainsKey(f)) ?? _inputErrors.Keys.FirstOrDefault();
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
