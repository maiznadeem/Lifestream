using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Contracts.Dashboard;

public sealed record DashboardKpis(
    int TotalDonors,
    int TotalPatients,
    int PendingAppointments,
    int PendingRequests,
    int UnitsAvailable,
    int UnitsExpiringSoon);

/// <summary>Per-blood-type monthly counts; both arrays have one entry per month in the response's Months (zeros included).</summary>
public sealed record BloodTypeSeries(BloodType BloodType, int[] Donated, int[] Issued);

public sealed record GenderCount(Gender Gender, int Count);

/// <param name="Kpis">Headline numbers.</param>
/// <param name="Months">First day of each month in the requested range, ascending.</param>
/// <param name="Series">One entry per blood type.</param>
/// <param name="Demographics">One entry per gender value, zeros included.</param>
/// <param name="LowStock">Stock items below their threshold.</param>
public sealed record DashboardResponse(
    DashboardKpis Kpis,
    IReadOnlyList<DateOnly> Months,
    IReadOnlyList<BloodTypeSeries> Series,
    IReadOnlyList<GenderCount> Demographics,
    IReadOnlyList<StockItem> LowStock);
