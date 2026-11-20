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
/// (the API's oldest-first statement), so compatible groups do not count here: "Can fulfil" means the Fulfil action will succeed with today's stock. The server
/// issues a request in full or not at all, so a request the stock covers only in part ("2 short") cannot be fulfilled yet, just like one with no stock.
/// </summary>
public sealed record RequestCoverage(CoverKind Kind, string Text, int Units, int Available)
{
    public static RequestCoverage Of(int units, int available) =>
        available >= units ? new(CoverKind.CanFulfil, "Can fulfil", units, available)
        : available <= 0 ? new(CoverKind.NoStock, "No stock", units, 0)
        : new(CoverKind.Partial, $"{units - available} short", units, available);

    /// <summary>"4 units of AB+, 3 needed": the side panel's "In stock" line (its label already says "in stock").</summary>
    public string Sentence(BloodType group) => $"{RecordFormatting.Units(Available)} of {group.ToTypographic()}, {Units} needed";

    /// <summary>How many units are missing; 0 when the stock covers the request.</summary>
    public int UnitsShort => Math.Max(0, Units - Available);

    /// <summary>
    /// Why Fulfil is not offered: none of the group in stock ("No O− in stock. The request stays open until stock arrives."), or less than the request needs
    /// ("Only 2 of the 3 units of O− needed are in stock, 1 unit short. ..."): the server refuses a fulfil it cannot issue in full. Null when the stock covers it.
    /// </summary>
    public string? HoldReason(BloodType group) => Kind switch
    {
        CoverKind.NoStock => $"No {group.ToTypographic()} in stock. The request stays open until stock arrives.",
        CoverKind.Partial => $"{ShortSentence(group)} A request is issued in full, so it stays open until more arrives.",
        _ => null,
    };

    /// <summary>"Only 2 of the 3 units of O− needed are in stock, 1 unit short."</summary>
    public string ShortSentence(BloodType group) =>
        $"Only {Available} of the {RecordFormatting.Units(Units)} of {group.ToTypographic()} needed {(Available == 1 ? "is" : "are")} in stock, {RecordFormatting.Units(UnitsShort)} short.";
}
