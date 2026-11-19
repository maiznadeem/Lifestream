using Lifestream.Domain;

namespace Lifestream.Contracts.Lookups;

/// <summary>One row of a record picker: the id plus a title and subtitle to show instead of a bare id.</summary>
public sealed record RecordOption(int Id, string Title, string? Subtitle);

/// <summary>One value of a fixed list: the wire value (enum name) and the text to display.</summary>
public sealed record EnumOption(string Value, string Display);

/// <summary>
/// Every fixed list, defined once here from the Domain enums. The API returns it from lookups/enums and the client
/// fills its dropdowns from it, so the two cannot differ.
/// </summary>
public sealed record EnumLookups(
    IReadOnlyList<EnumOption> BloodTypes,
    IReadOnlyList<EnumOption> Genders,
    IReadOnlyList<EnumOption> Shifts,
    IReadOnlyList<EnumOption> Designations,
    IReadOnlyList<EnumOption> Roles,
    IReadOnlyList<EnumOption> DonorFrequencies,
    IReadOnlyList<EnumOption> AppointmentStatuses,
    IReadOnlyList<EnumOption> RequestStatuses,
    IReadOnlyList<EnumOption> ReceiptKinds,
    IReadOnlyList<EnumOption> BagStatuses)
{
    public static EnumLookups Create() => new(
        [.. BloodTypeExtensions.All.Select(b => new EnumOption(b.ToString(), b.ToDisplay()))],
        Of<Gender>(),
        Of<Shift>(),
        Of<Designation>(),
        Of<Role>(),
        Of<DonorFrequency>(),
        Of<AppointmentStatus>(),
        Of<RequestStatus>(),
        Of<ReceiptKind>(),
        Of<BagStatus>());

    private static EnumOption[] Of<TEnum>()
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(v => new EnumOption(v.ToString(), Humanize(v.ToString())))];

    /// <summary>"FullTime" becomes "Full time", "NoShow" becomes "No show".</summary>
    private static string Humanize(string name)
    {
        var chars = new List<char>(name.Length + 2);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                chars.Add(' ');
                chars.Add(char.ToLowerInvariant(name[i]));
            }
            else
            {
                chars.Add(name[i]);
            }
        }

        return new string([.. chars]);
    }
}
