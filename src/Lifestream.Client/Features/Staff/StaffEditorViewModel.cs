using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts;
using Lifestream.Client.Validation;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Staff;

/// <summary>The values of the staff form, normalised the way they are sent, so "changed" means changed.</summary>
public sealed record StaffValues(string DisplayName, Gender? Gender, DateOnly? DateOfBirth, Designation? Designation, Shift? Shift, Role? Role, int? Salary, string? Phone);

/// <summary>
/// The staff form (FEAT-21). A new member starts with an empty date of birth and salary (no made-up defaults), and the password is collected only
/// through two masked fields that must match. Editing never shows or sends a password and sends only the fields that changed; the username can't
/// be changed. The salary box takes digits only.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed partial class StaffEditorViewModel : RecordEditorViewModel
{
    public const int MaxName = 100;
    public const int MaxUsername = 50;
    public const int MinUsername = 3;
    public const int MaxSalary = 10_000_000;
    public const int MinimumAgeYears = 16;


    private readonly ILifestreamApiClient _api;
    private readonly int? _id;
    private string _rowVersion = string.Empty;
    private StaffValues _baseline = new(string.Empty, null, null, null, null, null, null, null);
    private StaffDetail? _reloaded;

    private StaffEditorViewModel(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider clock, StaffDetail? existing)
        : base(toasts, dialogs, clock, "staff member", existing is null)
    {
        _api = api;
        _id = existing?.Id;
        GenderChoices = SharedLists.Genders;
        DesignationChoices = SharedLists.Designations;
        ShiftChoices = SharedLists.Shifts;
        RoleChoices = SharedLists.Roles;
        DeclareFormFields(nameof(DisplayName), nameof(Username), nameof(DateOfBirth), nameof(Gender), nameof(Designation), nameof(Shift), nameof(Role), nameof(SalaryText), nameof(Phone), nameof(Password), nameof(ConfirmPassword));
        if (existing is not null)
        {
            Load(existing);
        }
        else
        {
            Role = Domain.Role.Staff; // the least-privileged role; everything else starts empty
            _baseline = CurrentValues();
        }
    }

    public static StaffEditorViewModel ForNew(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, TimeProvider? clock = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, null);

    public static StaffEditorViewModel ForEdit(ILifestreamApiClient api, IToastService toasts, IDialogService dialogs, StaffDetail staff, TimeProvider? clock = null) =>
        new(api, toasts, dialogs, clock ?? TimeProvider.System, staff);

    public new string Title => IsNew ? "New staff member" : "Edit staff member";

    public new string SaveText => IsNew ? "Add staff member" : "Save changes";

    public IReadOnlyList<Choice<Gender>> GenderChoices { get; }

    public IReadOnlyList<Choice<Designation>> DesignationChoices { get; }

    public IReadOnlyList<Choice<Shift>> ShiftChoices { get; }

    public IReadOnlyList<Choice<Role>> RoleChoices { get; }

    /// <summary>The password fields exist only on the new-staff form.</summary>
    public bool ShowPasswordFields => IsNew;

    /// <summary>The username is chosen once, on the new-staff form.</summary>
    public bool CanEditUsername => IsNew;

    /// <summary>The username box is read-only when editing.</summary>
    public bool IsUsernameReadOnly => !IsNew;

    public string PasswordHint => $"At least {ResetPasswordDialogViewModel.MinLength} characters. The person can change it after signing in.";

    /// <summary>The latest date of birth the date field accepts: the youngest allowed age today.</summary>
    public DateOnly LatestDateOfBirth => LocalToday.AddYears(-MinimumAgeYears);

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Gender? Gender { get; set; }

    [ObservableProperty]
    public partial DateOnly? DateOfBirth { get; set; }

    [ObservableProperty]
    public partial Designation? Designation { get; set; }

    [ObservableProperty]
    public partial Shift? Shift { get; set; }

    [ObservableProperty]
    public partial Role? Role { get; set; }

    /// <summary>The salary as typed: digits only, empty when not given.</summary>
    [ObservableProperty]
    public partial string SalaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Phone { get; set; } = string.Empty;

    /// <summary>The initial password, typed in a masked field. Cleared when the form ends.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    public override bool HasUnsavedChanges => CurrentValues() != _baseline || (IsNew && (Username.Length > 0 || Password.Length > 0 || ConfirmPassword.Length > 0));

    protected override string RecordName => string.IsNullOrWhiteSpace(DisplayName) ? "staff member" : DisplayName.Trim();

    private int? Salary => int.TryParse(SalaryText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private StaffValues CurrentValues() => new(DisplayName.Trim(), Gender, DateOfBirth, Designation, Shift, Role, Salary, string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim());

    private void Load(StaffDetail staff)
    {
        _rowVersion = staff.RowVersion;
        DisplayName = staff.DisplayName;
        Username = staff.Username;
        Gender = staff.Gender;
        DateOfBirth = staff.DateOfBirth;
        Designation = staff.Designation;
        Shift = staff.Shift;
        Role = staff.Role;
        SalaryText = staff.Salary?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Phone = staff.Phone ?? string.Empty;
        _baseline = CurrentValues();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnDisplayNameChanged(string value) => FieldEdited(nameof(DisplayName));

    partial void OnUsernameChanged(string value) => FieldEdited(nameof(Username));

    partial void OnGenderChanged(Gender? value) => FieldEdited(nameof(Gender));

    partial void OnDateOfBirthChanged(DateOnly? value) => FieldEdited(nameof(DateOfBirth));

    partial void OnDesignationChanged(Designation? value) => FieldEdited(nameof(Designation));

    partial void OnShiftChanged(Shift? value) => FieldEdited(nameof(Shift));

    partial void OnRoleChanged(Role? value) => FieldEdited(nameof(Role));

    partial void OnSalaryTextChanged(string value) => FieldEdited(nameof(SalaryText));

    partial void OnPhoneChanged(string value) => FieldEdited(nameof(Phone));

    partial void OnPasswordChanged(string value) => FieldEdited(nameof(Password));

    partial void OnConfirmPasswordChanged(string value) => FieldEdited(nameof(ConfirmPassword));

    protected override string? MapServerField(string serverField) => serverField.ToLowerInvariant() switch
    {
        "displayname" => nameof(DisplayName),
        "username" => nameof(Username),
        "gender" => nameof(Gender),
        "dateofbirth" => nameof(DateOfBirth),
        "designation" => nameof(Designation),
        "shift" => nameof(Shift),
        "role" => nameof(Role),
        "salary" => nameof(SalaryText),
        "phone" => nameof(Phone),
        "initialpassword" => nameof(Password),
        _ => null,
    };

    protected override string? ValidateFormField(string field)
    {
        switch (field)
        {
            case nameof(DisplayName):
                if (string.IsNullOrWhiteSpace(DisplayName))
                {
                    return "Enter the person's full name.";
                }

                return DisplayName.Trim().Length > MaxName ? $"The name can be at most {MaxName} characters." : null;
            case nameof(Username):
                return IsNew ? ValidateUsername() : null;
            case nameof(DateOfBirth):
                return ValidateDateOfBirth();
            case nameof(Gender):
                return Gender is null ? "Choose a gender." : null;
            case nameof(Designation):
                return Designation is null ? "Choose a designation." : null;
            case nameof(Shift):
                return Shift is null ? "Choose a shift." : null;
            case nameof(Role):
                return Role is null ? "Choose a role." : null;
            case nameof(SalaryText):
                return ValidateSalary();
            case nameof(Phone):
                return ValidatePhone();
            case nameof(Password):
                return IsNew ? ValidatePassword() : null;
            case nameof(ConfirmPassword):
                if (!IsNew)
                {
                    return null;
                }

                if (ConfirmPassword.Length == 0)
                {
                    return "Type the password again.";
                }

                return string.Equals(ConfirmPassword, Password, StringComparison.Ordinal) ? null : "The two passwords don't match.";
            default:
                return null;
        }
    }

    private string? ValidateUsername()
    {
        var name = Username.Trim();
        if (name.Length == 0)
        {
            return "Enter a username.";
        }

        if (name.Length is < MinUsername or > MaxUsername)
        {
            return $"The username must be {MinUsername} to {MaxUsername} characters.";
        }

        return name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? null : "Use only letters, digits, dot, underscore and dash.";
    }

    private string? ValidateDateOfBirth()
    {
        if (DateOfBirth is not { } dob)
        {
            return "Enter the date of birth.";
        }

        if (dob < PersonEditorViewModel.EarliestDateOfBirth)
        {
            return $"Enter a real date of birth (not before {Formatting.Date(PersonEditorViewModel.EarliestDateOfBirth)}).";
        }

        if (dob >= LocalToday)
        {
            return "The date of birth must be in the past.";
        }

        return Eligibility.AgeOn(dob, LocalToday) < MinimumAgeYears ? $"Staff must be at least {MinimumAgeYears} years old." : null;
    }

    private string? ValidateSalary()
    {
        var text = SalaryText.Trim();
        if (text.Length == 0)
        {
            return null; // optional
        }

        if (!text.All(char.IsAsciiDigit))
        {
            return "Use digits only.";
        }

        return Salary is { } value && value <= MaxSalary ? null : $"The salary can be at most {MaxSalary:N0}.";
    }

    private string? ValidatePhone()
    {
        var phone = Phone.Trim();
        if (phone.Length == 0)
        {
            return null;
        }

        return PhoneRule.Validate(phone);
    }

    private string? ValidatePassword()
    {
        if (Password.Length == 0)
        {
            return "Enter a password.";
        }

        if (Password.Length < ResetPasswordDialogViewModel.MinLength)
        {
            return $"Use at least {ResetPasswordDialogViewModel.MinLength} characters.";
        }

        return Password.Length > ResetPasswordDialogViewModel.MaxLength ? $"Use at most {ResetPasswordDialogViewModel.MaxLength} characters." : null;
    }

    /// <summary>The PATCH body of an edit: the row version and only the fields that changed.</summary>
    public UpdateStaffRequest BuildUpdate()
    {
        var now = CurrentValues();
        var was = _baseline;
        return new UpdateStaffRequest(
            _rowVersion,
            DisplayName: now.DisplayName == was.DisplayName ? default : Optional.Of(now.DisplayName),
            Gender: now.Gender == was.Gender ? default : Optional.Of(now.Gender!.Value),
            DateOfBirth: now.DateOfBirth == was.DateOfBirth ? default : Optional.Of(now.DateOfBirth!.Value),
            Designation: now.Designation == was.Designation ? default : Optional.Of(now.Designation!.Value),
            Shift: now.Shift == was.Shift ? default : Optional.Of(now.Shift!.Value),
            Role: now.Role == was.Role ? default : Optional.Of(now.Role!.Value),
            Salary: now.Salary == was.Salary ? default : Optional.Of<int?>(now.Salary),
            Phone: string.Equals(now.Phone, was.Phone, StringComparison.Ordinal) ? default : Optional.Of<string?>(now.Phone));
    }

    /// <summary>The body of a new staff member.</summary>
    public CreateStaffRequest BuildCreate()
    {
        var v = CurrentValues();
        return new CreateStaffRequest(v.DisplayName, Username.Trim(), v.Gender!.Value, v.DateOfBirth!.Value, v.Designation!.Value, v.Shift!.Value, v.Role!.Value, v.Salary, v.Phone, Password);
    }

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareSubmit()
    {
        if (_id is { } id)
        {
            var update = BuildUpdate();
            return async ct => Finish(await _api.UpdateStaffAsync(id, update, ct).ConfigureAwait(false));
        }

        var create = BuildCreate();
        return async ct =>
        {
            var result = await _api.CreateStaffAsync(create, ct).ConfigureAwait(false);
            if (!result.IsOk && result.Problem!.Code == ProblemCodes.Conflict)
            {
                // The only unique value on the form is the username: say so under that field.
                return result.Problem with { Code = ProblemCodes.Validation, Errors = new Dictionary<string, string[]> { ["username"] = ["That username is already taken. Choose another."] } };
            }

            return Finish(result);
        };
    }

    private ApiProblem? Finish(ApiResult<StaffDetail> result)
    {
        if (!result.IsOk)
        {
            return result.Problem;
        }

        SavedRecord = result.Value;
        _rowVersion = result.Value.RowVersion;
        return null;
    }

    protected override Func<CancellationToken, Task<ApiProblem?>> PrepareReload()
    {
        var id = _id ?? 0;
        return async ct =>
        {
            var result = await _api.GetStaffMemberAsync(id, ct).ConfigureAwait(false);
            _reloaded = result.IsOk ? result.Value : null;
            return result.Problem;
        };
    }

    protected override void ApplyReloaded()
    {
        if (_reloaded is { } staff)
        {
            Load(staff);
        }
    }

    protected override void AcceptAsBaseline()
    {
        _baseline = CurrentValues();
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Password = string.Empty;
            ConfirmPassword = string.Empty;
        }

        base.Dispose(disposing);
    }
}
