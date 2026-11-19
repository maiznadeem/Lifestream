using Lifestream.Client.Common;
using Lifestream.Contracts.Lookups;
using Lifestream.Domain;

namespace Lifestream.Client.Features.ListScreens;

/// <summary>
/// Every fixed list a dropdown shows (blood types, genders, shifts, designations, roles, frequencies and statuses), taken from the shared contracts' <see cref="EnumLookups"/> (API-45).
/// The server builds the answer of <c>lookups/enums</c> from the same <see cref="EnumLookups.Create"/>, so a client dropdown and the server can't disagree on values, order or text;
/// a screen never lists an enum itself.
/// </summary>
public static class SharedLists
{
    private static readonly EnumLookups Lookups = EnumLookups.Create();

    public static IReadOnlyList<Choice<BloodType>> BloodTypes { get; } = Of<BloodType>(Lookups.BloodTypes);

    public static IReadOnlyList<Choice<Gender>> Genders { get; } = Of<Gender>(Lookups.Genders);

    public static IReadOnlyList<Choice<Shift>> Shifts { get; } = Of<Shift>(Lookups.Shifts);

    public static IReadOnlyList<Choice<Designation>> Designations { get; } = Of<Designation>(Lookups.Designations);

    public static IReadOnlyList<Choice<Role>> Roles { get; } = Of<Role>(Lookups.Roles);

    public static IReadOnlyList<Choice<DonorFrequency>> DonorFrequencies { get; } = Of<DonorFrequency>(Lookups.DonorFrequencies);

    public static IReadOnlyList<Choice<AppointmentStatus>> AppointmentStatuses { get; } = Of<AppointmentStatus>(Lookups.AppointmentStatuses);

    public static IReadOnlyList<Choice<RequestStatus>> RequestStatuses { get; } = Of<RequestStatus>(Lookups.RequestStatuses);

    public static IReadOnlyList<Choice<ReceiptKind>> ReceiptKinds { get; } = Of<ReceiptKind>(Lookups.ReceiptKinds);

    /// <summary>A filter drop-down over one of the lists: an explicit "All" first, then each value with the shared text.</summary>
    public static IReadOnlyList<FilterOption<T>> WithAll<T>(IReadOnlyList<Choice<T>> choices, string allText)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(choices);
        return [new FilterOption<T>(allText, null), .. choices.Select(c => new FilterOption<T>(c.Display, c.Value))];
    }

    private static List<Choice<T>> Of<T>(IReadOnlyList<EnumOption> options)
        where T : struct, Enum =>
        [.. options.Select(o => new Choice<T>(o.Display, Enum.Parse<T>(o.Value)))];
}
