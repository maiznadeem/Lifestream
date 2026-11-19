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

    /// <summary>
    /// A job title in words. The Admin designation is an office job, not the Admin role, so it reads "Administration" and never "Admin" next to a role
    /// badge that says "Staff".
    /// </summary>
    public static string JobTitle(Designation designation) => designation == Designation.Admin ? "Administration" : designation.ToString();

    /// <summary>The role in words: "Administrator" or "Staff".</summary>
    public static string Role(Role role) => role == Domain.Role.Admin ? "Administrator" : "Staff";

    /// <summary>"Salary: 2,000–3,000", "Salary: from 2,000", "Salary: up to 3,000", or "Salary" with no range: the salary chip's words.</summary>
    public static string SalaryChip(int? from, int? to) => (from, to) switch
    {
        ({ } f, { } t) => $"Salary: {Salary(f)}\u2013{Salary(t)}",
        ({ } f, null) => $"Salary: from {Salary(f)}",
        (null, { } t) => $"Salary: up to {Salary(t)}",
        _ => "Salary",
    };

    /// <summary>"HG" for "Hana Greenleaf": the first letters of the first and last words ("Dr." and similar titles are skipped); "?" for no name.</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.EndsWith('.') && char.IsLetter(p[0]))
            .ToList();
        return parts.Count switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }
}

/// <summary>
/// One row of the staff list. The list response carries no salary or phone either (it keeps the payload small), so the row has no such members and the grid
/// no such columns; they are read from the detail endpoint and shown in the detail pane (admin only). The list response carries no photo bytes (only <see cref="HasPhoto"/>); the thumbnail is fetched when the row is first
/// shown, which is why <see cref="Photo"/> raises change notification: the cell shows the shared default avatar until the bytes arrive (FEAT-20).
/// A row without a photo never makes a request.
/// </summary>
public sealed partial class StaffRow : ObservableObject
{
    private int _requested;

    public StaffRow(StaffListItem item)
    {
        Item = item;
        Id = item?.Id ?? 0; // a row built without an item (by reflection-based tests) has no id
    }

    public StaffListItem Item { get; }

    public int Id { get; }

    public string DisplayName => Item.DisplayName;

    public string Username => Item.Username;

    public string DesignationText => StaffText.JobTitle(Item.Designation);

    public string ShiftText => StaffText.Shift(Item.Shift);

    public string RoleText => StaffText.Role(Item.Role);

    /// <summary>"S-0002": the person's code under their username.</summary>
    public string CodeText => Receipts.RecordCodes.Staff(Item.Id);

    /// <summary>True for an administrator (the row shows the Admin role badge).</summary>
    public bool IsAdmin => Item.Role == Role.Admin;

    /// <summary>The avatar's letters until (or instead of) a photo.</summary>
    public string Initials => StaffText.Initials(Item.DisplayName);

    public bool HasPhoto => Item.HasPhoto;

    /// <summary>The thumbnail bytes once fetched; null shows the default avatar.</summary>
    [ObservableProperty]
    public partial byte[]? Photo { get; set; }

    /// <summary>True the first time it is called for a row with a photo, so the photo is fetched once, however often the row is shown again.</summary>
    internal bool ClaimPhotoRequest() => HasPhoto && Interlocked.Exchange(ref _requested, 1) == 0;

    /// <summary>Lets the row ask for its photo again (its request was cancelled before the photo arrived).</summary>
    internal void ReleasePhotoRequest() => Interlocked.Exchange(ref _requested, 0);
}
