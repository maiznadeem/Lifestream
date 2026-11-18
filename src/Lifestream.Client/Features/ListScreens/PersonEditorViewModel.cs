using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Common;
using Lifestream.Contracts;
using Lifestream.Domain;

namespace Lifestream.Client.Features.ListScreens;

/// <summary>The values of a person form, normalised the way they are sent, so "changed" means changed (FEAT-05).</summary>
public sealed record PersonValues(string Name, DateOnly? DateOfBirth, Gender? Gender, BloodType? BloodType, string? Phone, string? Address);

/// <summary>
/// The fields donors and patients share: name, date of birth, gender, blood type, phone and address (FEAT-07). Required fields are
/// marked, validated before submit (date of birth in the past, no DateTime.MinValue, a minimum age for donors, a numeric-only phone) and
/// each message sits under its field. <see cref="HasUnsavedChanges"/> compares with the loaded values, and an update sends only the
/// fields that differ (FEAT-05).
/// </summary>
public abstract partial class PersonEditorViewModel : RecordEditorViewModel
{
    /// <summary>The longest name, phone and address the database stores.</summary>
    public const int MaxName = 100;
    public const int MaxPhone = 20;
    public const int MaxAddress = 200;

    /// <summary>The earliest date of birth accepted (anything earlier is a typing slip or a default value).</summary>
    public static DateOnly EarliestDateOfBirth { get; } = new(1900, 1, 1);

    private static readonly Regex PhonePattern = new(@"^\+?[0-9][0-9 ()\-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private PersonValues _baseline = new(string.Empty, null, null, null, null, null);

    protected PersonEditorViewModel(IToastService toasts, IDialogService dialogs, TimeProvider clock, string recordType, bool isNew, int minimumAgeYears)
        : base(toasts, dialogs, clock, recordType, isNew)
    {
        MinimumAgeYears = minimumAgeYears;
        GenderChoices = [.. Enum.GetValues<Gender>().Select(g => new Choice<Gender>(g.ToString(), g))];
        BloodTypeChoices = [.. BloodTypeExtensions.All.Select(b => new Choice<BloodType>(b.ToDisplay(), b))];
    }

    public int MinimumAgeYears { get; }

    public IReadOnlyList<Choice<Gender>> GenderChoices { get; }

    public IReadOnlyList<Choice<BloodType>> BloodTypeChoices { get; }

    /// <summary>The last date of birth the date field accepts: yesterday (a date of birth must be in the past).</summary>
    public DateOnly LatestDateOfBirth => LocalToday.AddDays(-1);

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateOnly? DateOfBirth { get; set; }

    [ObservableProperty]
    public partial Gender? Gender { get; set; }

    [ObservableProperty]
    public partial BloodType? BloodType { get; set; }

    [ObservableProperty]
    public partial string Phone { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;

    public override bool HasUnsavedChanges => CurrentValues() != _baseline || ExtraDirty;

    /// <summary>True when a field of the subclass (donor frequency) differs from its baseline.</summary>
    protected virtual bool ExtraDirty => false;

    protected override string RecordName => string.IsNullOrWhiteSpace(Name) ? RecordType : Name.Trim();

    /// <summary>The form's current values, normalised: trimmed, empty optional fields as null.</summary>
    protected PersonValues CurrentValues() => new(Name.Trim(), DateOfBirth, Gender, BloodType, Blank(Phone), Blank(Address));

    /// <summary>The values the form was loaded with (or last saved).</summary>
    protected PersonValues Baseline => _baseline;

    /// <summary>Fills the shared fields from a loaded record and makes them the baseline.</summary>
    protected void LoadValues(PersonValues values)
    {
        Name = values.Name;
        DateOfBirth = values.DateOfBirth;
        Gender = values.Gender;
        BloodType = values.BloodType;
        Phone = values.Phone ?? string.Empty;
        Address = values.Address ?? string.Empty;
        _baseline = CurrentValues();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    protected override void AcceptAsBaseline()
    {
        _baseline = CurrentValues();
        AcceptExtraAsBaseline();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    protected virtual void AcceptExtraAsBaseline()
    {
    }

    protected static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    protected override string? ValidateFormField(string field)
    {
        switch (field)
        {
            case nameof(Name):
                if (string.IsNullOrWhiteSpace(Name))
                {
                    return $"Enter the {RecordType}'s full name.";
                }

                return Name.Trim().Length > MaxName ? $"The name can be at most {MaxName} characters." : null;
            case nameof(DateOfBirth):
                return ValidateDateOfBirth();
            case nameof(Gender):
                return Gender is null ? "Choose a gender." : null;
            case nameof(BloodType):
                return BloodType is null ? "Choose a blood type." : null;
            case nameof(Phone):
                return ValidatePhone();
            case nameof(Address):
                return Address.Trim().Length > MaxAddress ? $"The address can be at most {MaxAddress} characters." : null;
            default:
                return ValidateExtraField(field);
        }
    }

    protected virtual string? ValidateExtraField(string field) => null;

    private string? ValidateDateOfBirth()
    {
        if (DateOfBirth is not { } dob)
        {
            return "Enter the date of birth.";
        }

        if (dob == DateOnly.MinValue || dob < EarliestDateOfBirth)
        {
            return $"Enter a real date of birth (not before {Formatting.Date(EarliestDateOfBirth)}).";
        }

        var today = LocalToday;
        if (dob >= today)
        {
            return "The date of birth must be in the past.";
        }

        if (MinimumAgeYears > 0 && Eligibility.AgeOn(dob, today) < MinimumAgeYears)
        {
            return $"A {RecordType} must be at least {MinimumAgeYears} years old.";
        }

        return null;
    }

    private string? ValidatePhone()
    {
        var phone = Phone.Trim();
        if (phone.Length == 0)
        {
            return null; // optional
        }

        if (phone.Length > MaxPhone)
        {
            return $"The phone number can be at most {MaxPhone} characters.";
        }

        return PhonePattern.IsMatch(phone) ? null : "Use digits only (a leading + and spaces, dashes or brackets are allowed).";
    }

    partial void OnNameChanged(string value) => FieldEdited(nameof(Name));

    partial void OnDateOfBirthChanged(DateOnly? value) => FieldEdited(nameof(DateOfBirth));

    partial void OnGenderChanged(Gender? value) => FieldEdited(nameof(Gender));

    partial void OnBloodTypeChanged(BloodType? value) => FieldEdited(nameof(BloodType));

    partial void OnPhoneChanged(string value) => FieldEdited(nameof(Phone));

    partial void OnAddressChanged(string value) => FieldEdited(nameof(Address));

    /// <summary>The PATCH value for a required text field: absent when unchanged.</summary>
    protected static Optional<T> IfChanged<T>(T current, T original)
        where T : notnull => EqualityComparer<T>.Default.Equals(current, original) ? default : Optional.Of<T>(current);

    /// <summary>The PATCH value for an optional text field: absent when unchanged, an explicit null when cleared (FEAT-05).</summary>
    protected static Optional<string?> OptionalTextIfChanged(string? current, string? original) =>
        string.Equals(current, original, StringComparison.Ordinal) ? default : Optional.Of<string?>(current);
}
