using Lifestream.Client.Features.ListScreens;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Requests;

/// <summary>One bag the fulfil step will issue: its place in the order, its code, where it came from and its use-by date.</summary>
public sealed record SuggestedBagRow(int Order, string Code, string Origin, string UseBy);

/// <summary>Whether the stock can cover a request now: all of it, part of it ("2 short"), or none of it.</summary>
public enum CoverKind
{
    CanFulfil,
    Partial,
    NoStock,
}

/// <summary>
/// The Cover of a pending request (audit R1): the units of its own blood group in stock against the units it needs. Fulfil issues bags of the request's own group only
/// (the API's oldest-first statement), so compatible groups do not count here: "Can fulfil" means the Fulfil action will succeed with today's stock.
/// </summary>
public sealed record RequestCoverage(CoverKind Kind, string Text, int Units, int Available)
{
    public static RequestCoverage Of(int units, int available) =>
        available >= units ? new(CoverKind.CanFulfil, "Can fulfil", units, available)
        : available <= 0 ? new(CoverKind.NoStock, "No stock", units, 0)
        : new(CoverKind.Partial, $"{units - available} short", units, available);

    /// <summary>"4 units of AB+ in stock, 3 needed": the sentence of the side panel.</summary>
    public string Sentence(BloodType group) => $"{RecordFormatting.Units(Available)} of {group.ToTypographic()} in stock, {Units} needed";
}
