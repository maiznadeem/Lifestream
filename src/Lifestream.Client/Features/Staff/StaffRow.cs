using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Staff;

/// <summary>Plain-language text for the staff enums.</summary>
public static class StaffText
{
    public static string Shift(Shift shift) => shift == Domain.Shift.FullTime ? "Full time" : shift.ToString();

    public static string Salary(int? salary) => salary is { } value ? value.ToString("N0", CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>
/// One row of the staff list. The list response carries no photo bytes (only <see cref="HasPhoto"/>); the thumbnail is fetched when the row is first
/// shown, which is why <see cref="Photo"/> raises change notification: the cell shows the shared default avatar until the bytes arrive (FEAT-20).
/// A row without a photo never makes a request.
/// </summary>
public sealed partial class StaffRow(StaffListItem item) : ObservableObject
{
    private int _requested;

    public StaffListItem Item { get; } = item;

    public int Id => Item.Id;

    public string DisplayName => Item.DisplayName;

    public string Username => Item.Username;

    public string DesignationText => Item.Designation.ToString();

    public string ShiftText => StaffText.Shift(Item.Shift);

    public string RoleText => Item.Role.ToString();

    public string SalaryText => StaffText.Salary(Item.Salary);

    public string PhoneText => Item.Phone ?? string.Empty;

    public bool HasPhoto => Item.HasPhoto;

    /// <summary>The thumbnail bytes once fetched; null shows the default avatar.</summary>
    [ObservableProperty]
    public partial byte[]? Photo { get; set; }

    /// <summary>True the first time it is called for a row with a photo, so the photo is fetched once, however often the row is shown again.</summary>
    internal bool ClaimPhotoRequest() => HasPhoto && Interlocked.Exchange(ref _requested, 1) == 0;
}
