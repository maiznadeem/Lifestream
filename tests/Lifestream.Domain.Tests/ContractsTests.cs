using System.Reflection;
using System.Text.Json;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Lookups;

namespace Lifestream.Domain.Tests;

public sealed class ContractsTests
{
    private static readonly JsonSerializerOptions Json = ContractJson.CreateOptions();

    private static readonly string[] ExpectedProblemCodes =
    [
        "validation", "not_found", "conflict", "concurrency", "insufficient_stock", "already_receipted", "slot_taken",
        "ineligible", "locked_out", "rate_limited", "database_waking", "database_resting", "demo_forbidden",
        "demo_reset_cooldown",
    ];

    private static IEnumerable<Type> ContractTypes() =>
        typeof(ApiRoutes).Assembly.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("Lifestream.Contracts", StringComparison.Ordinal) == true);

    [Fact]
    public void Optional_AbsentNullAndValue_RoundTripThroughJson()
    {
        var absent = new UpdateDonorRequest("rv");
        var cleared = new UpdateDonorRequest("rv", Phone: Optional.Of<string?>(null));
        var set = new UpdateDonorRequest("rv", Phone: Optional.Of<string?>("+1-555-0101"), Name: Optional.Of("Ayşe Nur"));

        var absentJson = JsonSerializer.Serialize(absent, Json);
        var clearedJson = JsonSerializer.Serialize(cleared, Json);
        var setJson = JsonSerializer.Serialize(set, Json);

        Assert.Equal("""{"rowVersion":"rv"}""", absentJson);
        Assert.Equal("""{"rowVersion":"rv","phone":null}""", clearedJson);
        using (var document = JsonDocument.Parse(setJson))
        {
            // The encoder escapes "+" and non-ASCII letters, so compare parsed values rather than raw text.
            Assert.Equal("+1-555-0101", document.RootElement.GetProperty("phone").GetString());
            Assert.Equal("Ayşe Nur", document.RootElement.GetProperty("name").GetString());
            Assert.False(document.RootElement.TryGetProperty("address", out _));
        }

        var absentBack = JsonSerializer.Deserialize<UpdateDonorRequest>(absentJson, Json)!;
        var clearedBack = JsonSerializer.Deserialize<UpdateDonorRequest>(clearedJson, Json)!;
        var setBack = JsonSerializer.Deserialize<UpdateDonorRequest>(setJson, Json)!;

        Assert.False(absentBack.Phone.HasValue);
        Assert.True(clearedBack.Phone.HasValue);
        Assert.Null(clearedBack.Phone.Value);
        Assert.True(setBack.Phone.HasValue);
        Assert.Equal("+1-555-0101", setBack.Phone.Value);
        Assert.Equal("Ayşe Nur", setBack.Name.Value);
        Assert.False(setBack.Address.HasValue);
    }

    [Fact]
    public void Optional_EnumAndDateValues_RoundTrip()
    {
        var request = new UpdateDonorRequest("rv", BloodType: BloodType.ONeg, DateOfBirth: new DateOnly(1990, 3, 5));

        var back = JsonSerializer.Deserialize<UpdateDonorRequest>(JsonSerializer.Serialize(request, Json), Json)!;

        Assert.Equal(BloodType.ONeg, back.BloodType.Value);
        Assert.Equal(new DateOnly(1990, 3, 5), back.DateOfBirth.Value);
    }

    [Fact]
    public void Contracts_AllIdProperties_AreInt()
    {
        var offenders = new List<string>();
        foreach (var type in ContractTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var isId = property.Name == "Id" || (property.Name.EndsWith("Id", StringComparison.Ordinal) && property.Name != "Valid");
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (isId && propertyType != typeof(int))
                {
                    offenders.Add($"{type.Name}.{property.Name}: {property.PropertyType.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Contracts_DateOnlyValues_AreDateOnly_AndTimestampsAreUtcNamed()
    {
        var bad = ContractTypes()
            .SelectMany(t => t.GetProperties().Select(p => (Type: t, Property: p)))
            .Where(x => (Nullable.GetUnderlyingType(x.Property.PropertyType) ?? x.Property.PropertyType) == typeof(DateTime))
            .Where(x => !x.Property.Name.EndsWith("Utc", StringComparison.Ordinal))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        Assert.Empty(bad);
    }

    [Fact]
    public void EnumLookups_EqualDomainEnums()
    {
        var lookups = EnumLookups.Create();

        Assert.Equal(Enum.GetNames<BloodType>(), lookups.BloodTypes.Select(o => o.Value));
        Assert.Equal(BloodTypeExtensions.All.Select(b => b.ToDisplay()), lookups.BloodTypes.Select(o => o.Display));
        Assert.Equal(Enum.GetNames<Gender>(), lookups.Genders.Select(o => o.Value));
        Assert.Equal(Enum.GetNames<Shift>(), lookups.Shifts.Select(o => o.Value));
        Assert.Equal(Enum.GetNames<Designation>(), lookups.Designations.Select(o => o.Value));
        Assert.Equal(Enum.GetNames<Role>(), lookups.Roles.Select(o => o.Value));
        Assert.Equal(Enum.GetNames<DonorFrequency>(), lookups.DonorFrequencies.Select(o => o.Value));
        Assert.Equal(Enum.GetNames<AppointmentStatus>(), lookups.AppointmentStatuses.Select(o => o.Value));
        Assert.Equal(Enum.GetNames<RequestStatus>(), lookups.RequestStatuses.Select(o => o.Value));
        Assert.Equal(Enum.GetNames<ReceiptKind>(), lookups.ReceiptKinds.Select(o => o.Value));
        Assert.Equal(Enum.GetNames<BagStatus>(), lookups.BagStatuses.Select(o => o.Value));
        Assert.Equal("Full time", lookups.Shifts.Single(o => o.Value == nameof(Shift.FullTime)).Display);
        Assert.Equal("No show", lookups.AppointmentStatuses.Single(o => o.Value == nameof(AppointmentStatus.NoShow)).Display);
    }

    [Fact]
    public void EnumLookups_SurviveJsonRoundTrip()
    {
        var lookups = EnumLookups.Create();

        var back = JsonSerializer.Deserialize<EnumLookups>(JsonSerializer.Serialize(lookups, Json), Json)!;

        Assert.Equal(lookups.BloodTypes, back.BloodTypes);
        Assert.Equal(lookups.BagStatuses, back.BagStatuses);
    }

    [Fact]
    public void ApiRoutes_AreUnique_AndPathsBuild()
    {
        Assert.Equal(ApiRoutes.All.Count, ApiRoutes.All.Distinct().Count());
        Assert.Equal(41, ApiRoutes.All.Count);
        Assert.Equal("/api/v1/donors/7/eligibility", ApiRoutes.Path(ApiRoutes.DonorEligibility, 7));
        Assert.Equal("/api/v1/stock", ApiRoutes.Path(ApiRoutes.Stock));
        Assert.Equal("/healthz", ApiRoutes.Path(ApiRoutes.Healthz));
    }

    [Fact]
    public void ProblemCodes_AreTheFourteenSpecifiedValues()
    {
        var codes = typeof(ProblemCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order()
            .ToArray();

        Assert.Equal(ExpectedProblemCodes.Order(), codes);
    }

    [Fact]
    public void ApiHeaders_HaveSpecifiedNames()
    {
        Assert.Equal("Idempotency-Key", ApiHeaders.IdempotencyKey);
        Assert.Equal("X-Demo-Reset", ApiHeaders.DemoReset);
        Assert.Equal("X-Correlation-Id", ApiHeaders.CorrelationId);
        Assert.Equal(50, Paging.DefaultPageSize);
        Assert.Equal(200, Paging.MaxPageSize);
    }
}
