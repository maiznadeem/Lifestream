using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Staff;
using Lifestream.Contracts.Profile;

namespace Lifestream.Client.Features.Profile;

/// <summary>
/// Opens the change-password dialog from anywhere. The profile screen and the account menu use it, and the shell uses it with
/// <c>required</c> set when an admin reset the person's password and they must choose a new one before doing anything else.
/// </summary>
public interface IChangePasswordPrompt
{
    /// <returns>True when the password was changed.</returns>
    Task<bool> PromptAsync(bool required = false);
}

public sealed class ChangePasswordPrompt(ILifestreamApiClient api, IFormDialogService dialogs, IToastService toasts) : IChangePasswordPrompt
{
    public async Task<bool> PromptAsync(bool required = false)
    {
        using var dialog = new ChangePasswordDialogViewModel(api, required);
        var changed = await dialogs.ShowAsync(dialog).ConfigureAwait(true);
        if (changed)
        {
            toasts.Show(ToastKind.Success, dialog.SuccessMessage);
        }

        return changed;
    }
}

/// <summary>
/// The change-password dialog (FEAT-25): current, new and confirm passwords in masked fields, validated before anything is sent. It makes one call
/// that carries only the passwords, so it works the same for a user with or without a stored photo; it never reads or sends the photo. The typed
/// values are cleared when the dialog ends.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed partial class ChangePasswordDialogViewModel : DialogViewModel
{
    private readonly ILifestreamApiClient _api;

    public ChangePasswordDialogViewModel(ILifestreamApiClient api, bool required = false)
        : base("Change password", "Change password", "Changing...", required ? "Not now" : "Cancel")
    {
        _api = api;
        IsRequired = required;
        DeclareFields(nameof(CurrentPassword), nameof(NewPassword), nameof(ConfirmPassword));
    }

    /// <summary>True when an admin reset the password: the dialog says why it appeared.</summary>
    public bool IsRequired { get; }

    public string Explanation => IsRequired
        ? "An administrator reset your password. Enter the temporary password you were given, then choose a new one."
        : $"Choose a new password of at least {ResetPasswordDialogViewModel.MinLength} characters.";

    public string SuccessMessage => "Your password was changed.";

    [ObservableProperty]
    public partial string CurrentPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    protected override string RecordType => "password";

    protected override string? ValidateField(string field)
    {
        switch (field)
        {
            case nameof(CurrentPassword):
                return CurrentPassword.Length == 0 ? "Enter your current password." : null;
            case nameof(NewPassword):
                if (NewPassword.Length == 0)
                {
                    return "Enter a new password.";
                }

                if (NewPassword.Length < ResetPasswordDialogViewModel.MinLength)
                {
                    return $"Use at least {ResetPasswordDialogViewModel.MinLength} characters.";
                }

                if (NewPassword.Length > ResetPasswordDialogViewModel.MaxLength)
                {
                    return $"Use at most {ResetPasswordDialogViewModel.MaxLength} characters.";
                }

                return string.Equals(NewPassword, CurrentPassword, StringComparison.Ordinal) ? "The new password must be different from the current one." : null;
            case nameof(ConfirmPassword):
                if (ConfirmPassword.Length == 0)
                {
                    return "Type the new password again.";
                }

                return string.Equals(ConfirmPassword, NewPassword, StringComparison.Ordinal) ? null : "The two passwords don't match.";
            default:
                return null;
        }
    }

    partial void OnCurrentPasswordChanged(string value) => FieldEdited(nameof(CurrentPassword));

    partial void OnNewPasswordChanged(string value) => FieldEdited(nameof(NewPassword));

    partial void OnConfirmPasswordChanged(string value) => FieldEdited(nameof(ConfirmPassword));

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        var request = new ChangePasswordRequest(CurrentPassword, NewPassword);
        return async ct => (await _api.ChangePasswordAsync(request, ct).ConfigureAwait(false)).Problem;
    }

    protected override void ShowProblem(ApiProblem problem)
    {
        // The API says which field is wrong ("currentPassword is not correct"): put the message under that field.
        if (problem.ErrorsFor("currentPassword") is [_, ..])
        {
            SetError(nameof(CurrentPassword), "Your current password isn't correct.");
            return;
        }

        if (problem.ErrorsFor("newPassword") is [{ } newPasswordMessage, ..])
        {
            SetError(nameof(NewPassword), $"The new password {newPasswordMessage}.");
            return;
        }

        FormError = ListScreens.ProblemText.For(problem, RecordType);
    }

    protected override void OnSucceeded() => ClearPasswords();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearPasswords();
        }

        base.Dispose(disposing);
    }

    private void ClearPasswords()
    {
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
    }
}
