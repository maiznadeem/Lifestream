using System.Reflection;
using System.Text.RegularExpressions;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Tests;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// UI quirk: the staff list showed Salary and Phone columns that were always blank, because the list endpoint never fills them. Every list screen's
/// grid columns are read from the XAML and checked against the fields their list item DTO really carries: no column may show a value that depends
/// on a field the list endpoint never fills.
/// </summary>
public sealed partial class ListColumnTests
{
    /// <summary>The fields of each list item DTO that the list endpoint never fills (null by design). Every list screen's DTO must be listed, even with none.</summary>
    private static readonly Dictionary<Type, string[]> NeverFilledByTheList = new()
    {
        [typeof(StaffListItem)] = [nameof(StaffListItem.Salary), nameof(StaffListItem.Phone)], // keeps the 110-staff payload small; see StaffListPayloadTests (API)
        [typeof(DonorListItem)] = [],
        [typeof(PatientListItem)] = [],
        [typeof(AppointmentListItem)] = [],
        [typeof(RequestListItem)] = [],
        [typeof(ReceiptListItem)] = [],
    };

    private static IEnumerable<Type> ListScreens() =>
        typeof(RecordListViewModel<,>).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && BaseDefinition(t) is not null);

    private static Type? BaseDefinition(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(PagedListViewModel<>))
            {
                return current;
            }
        }

        return null;
    }

    private static string ViewXaml(Type viewModel)
    {
        var name = viewModel.Name.Replace("ViewModel", string.Empty, StringComparison.Ordinal);
        return RepoPaths.Find($"src/Lifestream.Desktop/Features/{name}/{name}View.xaml");
    }

    private static Type ItemOf(Type viewModel) => BaseDefinition(viewModel)!.GetGenericArguments()[0];

    /// <summary>The DTO behind a row: the row itself, or the row's Item.</summary>
    private static Type DtoOf(Type item) => item.GetProperty("Item")?.PropertyType ?? item;

    /// <summary>Builds a row around a DTO: its constructor takes the DTO first, then things like a time zone.</summary>
    private static object Wrap(Type row, object dto)
    {
        var ctor = row.GetConstructors().Single();
        var extra = ctor.GetParameters().Skip(1).Select(p => p.ParameterType == typeof(TimeZoneInfo) ? (object)TimeZoneInfo.Utc : null);
        return ctor.Invoke([dto, .. extra]);
    }

    private static object Sample(Type type, string[] nulled)
    {
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => nulled.Contains(p.Name, StringComparer.OrdinalIgnoreCase) ? null : Value(p.ParameterType, p.Name!)).ToArray();
        return ctor.Invoke(args);
    }

    private static object? Value(Type type, string name)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type switch
        {
            _ when type == typeof(string) => "value-" + name,
            _ when type == typeof(int) => 7,
            _ when type == typeof(bool) => true,
            _ when type == typeof(DateOnly) => new DateOnly(2026, 3, 4),
            _ when type == typeof(DateTime) => new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc),
            _ when type == typeof(byte[]) => new byte[] { 1 },
            _ when type.IsEnum => Enum.GetValues(type).GetValue(Enum.GetValues(type).Length > 1 ? 1 : 0),
            _ => throw new NotSupportedException($"No sample for {type.Name} {name}"),
        };
    }

    private static IEnumerable<string> BoundPaths(string xamlFile)
    {
        var text = File.ReadAllText(xamlFile);
        var start = text.IndexOf("<DataGrid.Columns>", StringComparison.Ordinal);
        var end = text.IndexOf("</DataGrid.Columns>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"{Path.GetFileName(xamlFile)} has no grid columns");
        return Binding().Matches(text[start..end]).Select(m => m.Groups[1].Value.Split('.')[0]).Distinct();
    }

    [GeneratedRegex(@"\{Binding\s+(?:Path=)?([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Binding();

    [Fact]
    public void EveryListScreen_IsInTheRegistryOfListFields_SoANewListCannotSkipThisCheck()
    {
        var screens = ListScreens().ToList();
        var missing = screens.Where(s => !NeverFilledByTheList.ContainsKey(DtoOf(ItemOf(s)))).Select(s => s.Name).ToList();

        Assert.Equal(6, screens.Count);
        Assert.True(missing.Count == 0, "List screens whose item DTO is not in NeverFilledByTheList: " + string.Join(", ", missing));
    }

    [Fact]
    public void NoListColumn_ShowsAValueThatDependsOnAFieldTheListEndpointNeverFills()
    {
        var checkedColumns = 0;
        var problems = new List<string>();
        foreach (var screen in ListScreens())
        {
            var item = ItemOf(screen);
            var dto = DtoOf(item);
            var never = NeverFilledByTheList[dto];
            object Row(string[] nulled) => item == dto ? Sample(dto, nulled) : Wrap(item, Sample(dto, nulled));
            var full = Row([]);
            var asTheListSendsIt = Row(never);
            foreach (var path in BoundPaths(ViewXaml(screen)))
            {
                var property = item.GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
                if (property is null)
                {
                    continue; // a command or a helper of the screen, not a row value
                }

                checkedColumns++;
                var withEverything = property.GetValue(full)?.ToString();
                var withTheListsValues = property.GetValue(asTheListSendsIt)?.ToString();
                if (never.Length > 0 && withEverything != withTheListsValues)
                {
                    problems.Add($"{screen.Name}: the column bound to {path} depends on {string.Join("/", never)}, which the list endpoint never fills, so it is always blank");
                }
            }
        }

        Assert.True(checkedColumns >= 25, $"only {checkedColumns} columns were checked");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheStaffGrid_HasNoSalaryOrPhoneColumn()
    {
        var headers = HeaderPattern().Matches(File.ReadAllText(ViewXaml(typeof(StaffViewModel)))).Select(m => m.Groups[1].Value).ToList();

        Assert.DoesNotContain("Salary", headers);
        Assert.DoesNotContain("Phone", headers);
        Assert.Contains("Name", headers);
    }

    [GeneratedRegex(@"<DataGrid(?:Template|Text)Column Header=""([^""]+)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex HeaderPattern();
}
