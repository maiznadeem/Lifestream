using System.Reflection;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Navigation;
using Lifestream.Contracts;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Fakes;

/// <summary>
/// Wraps the fake API client and records the arguments of every call (the fake itself only records names), so a test can assert what a
/// screen sent: the query of a list call, the body of a PATCH. The answers still come from the fake's handlers. This is a fake API, not the real one.
/// </summary>
public class RecordingApi : DispatchProxy
{
    private ILifestreamApiClient? _inner;
    private readonly Lock _gate = new();
    private readonly List<(string Name, object?[] Args)> _calls = [];

    public static (ILifestreamApiClient Api, RecordingApi Recorder) Wrap(FakeApiClient inner)
    {
        var proxy = Create<ILifestreamApiClient, RecordingApi>();
        var recorder = (RecordingApi)(object)proxy;
        recorder._inner = inner;
        return (proxy, recorder);
    }

    public IReadOnlyList<(string Name, object?[] Args)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IEnumerable<T> ArgsOf<T>(string name) => Calls.Where(c => c.Name == name).Select(c => c.Args.OfType<T>().First());

    public T LastArg<T>(string name) => ArgsOf<T>(name).Last();

    public int CountOf(string name) => Calls.Count(c => c.Name == name);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        lock (_gate)
        {
            _calls.Add((targetMethod.Name, args ?? []));
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}

/// <summary>A dialog service that answers every confirmation with a preset answer and remembers what it was asked.</summary>
public sealed class FakeDialogs : IDialogService
{
    public bool Answer { get; set; } = true;

    public List<(string Title, string Body, string Verb)> Confirmations { get; } = [];

    public Task<bool> ConfirmAsync(string title, string body, string dangerVerb)
    {
        Confirmations.Add((title, body, dangerVerb));
        return Task.FromResult(Answer);
    }

    public Task ShowErrorAsync(string title, string message, string? details = null) => Task.CompletedTask;
}

/// <summary>A navigation service that records where a screen asked to go.</summary>
public sealed class FakeNavigation : INavigationService
{
    public List<(Type ViewModel, object? Parameter)> Visits { get; } = [];

    public ViewModelBase? Current => null;

    public event EventHandler? CurrentChanged
    {
        add { }
        remove { }
    }

    public Task<bool> NavigateTo<TViewModel>(object? parameter = null)
        where TViewModel : ViewModelBase => NavigateTo(typeof(TViewModel), parameter);

    public Task<bool> NavigateTo(Type viewModelType, object? parameter = null)
    {
        Visits.Add((viewModelType, parameter));
        return Task.FromResult(true);
    }

    public bool HasUnsavedChanges => false;

    public Task<bool> ConfirmDiscardAsync() => Task.FromResult(true);

    public Task ReloadCurrentAsync() => Task.CompletedTask;

    public void Reset()
    {
    }
}

/// <summary>Sample records for the feature tests.</summary>
public static class Samples
{
    public static DonorListItem DonorRow(int id, string? name = null, BloodType type = BloodType.OPos) =>
        new(id, name ?? $"Donor {id:D3}", new DateOnly(1990, 5, 17), Gender.Female, type, "+1-555-0101", DonorFrequency.Regular, new DateOnly(2026, 1, 10));

    public static DonorDetail Donor(int id = 7, string name = "Ayşe Nur Öztürk-Abdulrahman") =>
        new(id, name, new DateOnly(1990, 5, 17), Gender.Female, BloodType.OPos, "+1-555-0101", "12 Mill Road", DonorFrequency.Regular, new DateOnly(2026, 1, 10), "rv-1");

    public static PatientListItem PatientRow(int id, string? name = null) =>
        new(id, name ?? $"Patient {id:D3}", new DateOnly(1975, 3, 2), Gender.Male, BloodType.ANeg, "+1-555-0102", 1);

    public static PatientDetail Patient(int id = 3, string name = "Omar Haddad") =>
        new(id, name, new DateOnly(1975, 3, 2), Gender.Male, BloodType.ANeg, "+1-555-0102", "4 Elm Street", "rv-p1");

    public static PagedResult<DonorListItem> DonorPage(int total, int page = 1, int pageSize = 50)
    {
        var first = ((page - 1) * pageSize) + 1;
        var count = Math.Max(0, Math.Min(pageSize, total - first + 1));
        return new([.. Enumerable.Range(first, count).Select(i => DonorRow(i))], total, page, pageSize);
    }

    public static PagedResult<PatientListItem> PatientPage(int total, int page = 1, int pageSize = 50)
    {
        var first = ((page - 1) * pageSize) + 1;
        var count = Math.Max(0, Math.Min(pageSize, total - first + 1));
        return new([.. Enumerable.Range(first, count).Select(i => PatientRow(i))], total, page, pageSize);
    }

    public static IReadOnlyList<StockItem> Stock() =>
    [
        new(BloodType.APos, 40, 2, 10, false),
        new(BloodType.ANeg, 4, 0, 10, true),
        new(BloodType.BPos, 25, 0, 10, false),
        new(BloodType.BNeg, 12, 3, 10, false),
        new(BloodType.ABPos, 18, 0, 10, false),
        new(BloodType.ABNeg, 2, 1, 10, true),
        new(BloodType.OPos, 60, 5, 10, false),
        new(BloodType.ONeg, 9, 0, 10, true),
    ];

    public static DashboardResponse Dashboard(int months = 12)
    {
        var first = new DateOnly(2025, 12, 1);
        var monthList = Enumerable.Range(0, months).Select(i => first.AddMonths(i)).ToList();
        var series = BloodTypeExtensions.All.Select((t, i) => new BloodTypeSeries(
            t,
            [.. Enumerable.Range(0, months).Select(m => 2 + ((i + m) % 5))],
            [.. Enumerable.Range(0, months).Select(m => 1 + ((i + (2 * m)) % 4))])).ToList();
        var stock = Stock();
        return new(
            new DashboardKpis(120, 45, 8, 6, stock.Sum(s => s.Available), stock.Sum(s => s.ExpiringSoon)),
            monthList,
            series,
            [new(Gender.Male, 70), new(Gender.Female, 48), new(Gender.Other, 2)],
            [.. stock.Where(s => s.BelowThreshold)]);
    }
}
