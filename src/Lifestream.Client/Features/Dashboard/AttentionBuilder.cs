using System.Globalization;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Dashboard;

/// <summary>
/// What needs doing now, most urgent first. The dashboard's "Needs attention" panel and the title bar's notifications are both built here, from the
/// same three lists, so the bell can never show something the dashboard does not (or the other way round).
/// </summary>
public static class AttentionBuilder
{
    /// <summary>
    /// A request that no compatible stock can cover (at most two), the bags expiring within 7 days (and the request that could use them), the groups
    /// below their minimum, and the next request that is due within 3 days and can be fulfilled.
    /// </summary>
    /// <param name="stock">The stock by blood group.</param>
    /// <param name="requests">The open requests.</param>
    /// <param name="today">Today's appointments (the local day), earliest first, without the cancelled ones.</param>
    /// <param name="clock">The app clock: "today" and the appointment times are the person's local ones.</param>
    public static IReadOnlyList<AttentionItem> Build(IReadOnlyList<StockItem> stock, IReadOnlyList<RequestListItem> requests, IReadOnlyList<AppointmentListItem> today, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(today);
        ArgumentNullException.ThrowIfNull(clock);

        var items = new List<AttentionItem>();
        var day = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var available = stock.ToDictionary(i => i.BloodType, i => i.Available);
        int Cover(BloodType patient) => available.Where(p => p.Key.CanGiveTo(patient)).Sum(p => p.Value);
        static string Day(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        string Time(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), clock.LocalTimeZone).ToString("HH:mm", CultureInfo.CurrentCulture);

        foreach (var request in requests.Where(r => Cover(r.BloodType) < r.Units).OrderBy(r => r.RequiredBy).Take(2))
        {
            var cover = Cover(request.BloodType);
            var title = available.GetValueOrDefault(request.BloodType) == 0 ? $"{request.BloodType.ToTypographic()} is out of stock" : $"Not enough blood for {request.PatientName}";
            var detail = $"{request.PatientName} needs {Units(request.Units)} by {Day(request.RequiredBy)}" + (cover > 0 ? $"; {Units(cover)} can be given." : ".");
            var donor = today.FirstOrDefault(a => a.Status != AppointmentStatus.Completed && a.DonorBloodType.CanGiveTo(request.BloodType));
            if (donor is not null)
            {
                detail += $" {donor.DonorName} ({donor.DonorBloodType.ToTypographic()}) comes in at {Time(donor.SlotStartUtc)}.";
            }

            items.Add(new(AttentionKind.Critical, "Alert", title, detail, "Open request", "Requests", new ListPrefilter(RecordId: request.Id)));
        }

        foreach (var item in stock.Where(i => i.ExpiringSoon > 0).OrderByDescending(i => i.ExpiringSoon).Take(1))
        {
            var detail = "Use them first.";
            var user = requests.Where(r => item.BloodType.CanGiveTo(r.BloodType)).OrderBy(r => r.BloodType == item.BloodType ? 0 : 1).ThenBy(r => r.RequiredBy).FirstOrDefault();
            if (user is not null)
            {
                detail = $"Use them for {user.PatientName}’s request ({Units(user.Units)}, due {user.RequiredBy.ToString("ddd", CultureInfo.CurrentCulture)}).";
            }

            items.Add(new(AttentionKind.Warning, "Clock", $"{item.ExpiringSoon} {item.BloodType.ToTypographic()} {(item.ExpiringSoon == 1 ? "bag expires" : "bags expire")} within 7 days", detail,
                "Review bags", "Stock", new ListPrefilter(BloodType: item.BloodType, StockView: StockFilter.ExpiringSoon)));
        }

        var low = stock.Where(i => i.BelowThreshold).OrderBy(i => i.BloodType).ToList();
        if (low.Count > 0)
        {
            var threshold = low.Max(i => i.Threshold);
            items.Add(new(AttentionKind.Critical, "Stock", low.Count == 1 ? $"{low[0].BloodType.ToTypographic()} is below {threshold} units" : $"{low.Count} of {stock.Count} blood groups are below {threshold} units",
                string.Join(", ", low.Select(i => $"{i.BloodType.ToTypographic()} {i.Available}")) + ".", "Open stock", "Stock", new ListPrefilter(StockView: StockFilter.BelowThreshold)));
        }

        foreach (var request in requests.Where(r => Cover(r.BloodType) >= r.Units && r.RequiredBy <= day.AddDays(3)).OrderBy(r => r.RequiredBy).Take(1))
        {
            var due = request.RequiredBy == day ? "today" : request.RequiredBy == day.AddDays(1) ? "tomorrow" : $"on {request.RequiredBy.ToString("dddd", CultureInfo.CurrentCulture)}";
            var same = available.GetValueOrDefault(request.BloodType);
            items.Add(new(AttentionKind.Info, "Request", $"{request.PatientName}’s request is due {due}",
                $"{request.BloodType.ToTypographic()}, {Units(request.Units)}. {same} {request.BloodType.ToTypographic()} {(same == 1 ? "bag is" : "bags are")} available.", "Fulfil", "Requests", new ListPrefilter(RecordId: request.Id)));
        }

        return items;
    }

    private static string Units(int n) => n == 1 ? "1 unit" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} units";
}
