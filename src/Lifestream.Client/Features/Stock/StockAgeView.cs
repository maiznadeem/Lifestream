using System.Globalization;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Receipts;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Stock;

/// <summary>
/// One cell of the stock-by-age matrix: how many of a group's bags are in one band of <see cref="StockAges.Bands"/>. <see cref="Level"/> (0 to 4) sets
/// the cell's tint, so the eye finds where the stock sits; the number is always written, so the tint only decorates.
/// </summary>
public sealed record StockAgeCell(int Band, int? Count, bool IsExpiringBand)
{
    /// <summary>The count, or an en dash when the stock answer carried no age bands.</summary>
    public string Text => Count is { } count ? count.ToString("N0", CultureInfo.CurrentCulture) : "–";

    public bool IsZero => Count is 0;

    /// <summary>0 for none, then 1 (one bag), 2 (2 to 3), 3 (4 to 7) and 4 (8 or more).</summary>
    public int Level => Count switch
    {
        null or 0 => 0,
        1 => 1,
        < 4 => 2,
        < 8 => 3,
        _ => 4,
    };

    public string AutomationName
    {
        get
        {
            var band = StockAges.Bands[Band];
            var what = Count switch
            {
                null => "not known",
                0 => "none",
                1 => "1 bag",
                { } n => $"{n:N0} bags",
            };
            return $"{band.FromDay} to {band.ToDay} days old: {what}" + (IsExpiringBand ? ", expires within " + BloodRules.ExpiringSoonDays + " days" : string.Empty);
        }
    }
}

/// <summary>One bag in the group's panel, in use-first order: its code, when it was collected, when it expires and how long it has left.</summary>
public sealed class StockBagRow
{
    public StockBagRow(StockBag bag, int position, DateOnly today, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(bag);
        Bag = bag;
        Position = position;
        DaysLeft = bag.ExpiresOn.DayNumber - today.DayNumber;
        CollectedText = Formatting.Date(RecordFormatting.LocalDate(bag.CollectedAtUtc, zone));
    }

    public StockBag Bag { get; }

    /// <summary>1 for the bag to use first.</summary>
    public int Position { get; }

    public bool IsFirst => Position == 1;

    public int DaysLeft { get; }

    /// <summary>"B-0412": the bag's number.</summary>
    public string Code => RecordCodes.Bag(Bag.Id);

    public string CollectedText { get; }

    public string ExpiresText => Formatting.Date(Bag.ExpiresOn);

    public bool IsExpiring => DaysLeft <= BloodRules.ExpiringSoonDays;

    /// <summary>"Expires today", "1 day left", "12 days left".</summary>
    public string DaysLeftText => DaysLeft switch
    {
        <= 0 => "Expires today",
        1 => "1 day left",
        _ => $"{DaysLeft} days left",
    };

    public string AutomationName => $"{(IsFirst ? "Use first: " : string.Empty)}bag {Code}, collected {CollectedText}, expires {ExpiresText}, {DaysLeftText}";
}
