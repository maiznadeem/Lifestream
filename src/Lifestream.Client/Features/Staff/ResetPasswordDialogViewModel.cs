using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Contracts.Staff;

namespace Lifestream.Client.Features.Staff;

/// <summary>
/// The admin's "Reset password" dialog (FEAT-22). The admin types a temporary password twice in masked fields; the person must choose a new one at
/// next sign-in. The API answers with no content, so no password is ever shown, returned or kept: the typed values are cleared as soon as the dialog
/// ends, and nothing here reads a stored password.
/// </summary>
public sealed partial class ResetPasswordDialogViewModel : DialogViewModel
{
    /// <summary>The shortest password the API accepts.</summary>
    public const int MinLength = 12;

    public const int MaxLength = 128;

    private readonly ILifestreamApiClient _api;
    private readonly int _staffId;

    public ResetPasswordDialogViewModel(ILifestreamApiClient api, int staffId, string staffName)
        : base("Reset password", "Reset password", "Resetting...")
    {
        _api = api;
        _staffId = staffId;
        StaffName = staffName;
        DeclareFields(nameof(TemporaryPassword), nameof(ConfirmPassword));
    }

    public string StaffName { get; }

    public string Explanation => $"Choose a temporary password for {StaffName} and tell them in person. Their current password stops working, and they must choose a new one the next time they sign in.";

    /// <summary>The message the screen shows after a successful reset. It never contains a password.</summary>
    public string SuccessMessage => $"Password reset for {StaffName}. They must choose a new one at next sign-in.";

    [ObservableProperty]
    public partial string TemporaryPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    protected override string RecordType => "staff member";

    public override bool HasUnsavedChanges => TemporaryPassword.Length > 0 || ConfirmPassword.Length > 0;

    protected override string? ValidateField(string field)
    {
        switch (field)
        {
            case nameof(TemporaryPassword):
                if (TemporaryPassword.Length == 0)
                {
                    return "Enter a temporary password.";
                }

                if (TemporaryPassword.Length < MinLength)
                {
                    return $"Use at least {MinLength} characters.";
                }

                return TemporaryPassword.Length > MaxLength ? $"Use at most {MaxLength} characters." : null;
            case nameof(ConfirmPassword):
                if (ConfirmPassword.Length == 0)
                {
                    return "Type the temporary password again.";
                }

                return string.Equals(ConfirmPassword, TemporaryPassword, StringComparison.Ordinal) ? null : "The two passwords don't match.";
            default:
                return null;
        }
    }

    partial void OnTemporaryPasswordChanged(string value) => FieldEdited(nameof(TemporaryPassword));

    partial void OnConfirmPasswordChanged(string value) => FieldEdited(nameof(ConfirmPassword));

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        var request = new ResetPasswordRequest(TemporaryPassword);
        var id = _staffId;
        return async ct => (await _api.ResetStaffPasswordAsync(id, request, ct).ConfigureAwait(false)).Problem;
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
        TemporaryPassword = string.Empty;
        ConfirmPassword = string.Empty;
    }
}
