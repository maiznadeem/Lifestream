using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lifestream.Api.Common;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

/// <summary>
/// API-09: an enum is its defined name and nothing else, in the query string and in a body. Numbers (defined or not), numeric strings, undefined names,
/// combined names ("Male, Female"), comma lists where one value is expected and null are all 400 naming the field; nothing is stored. The fuzz walks
/// every route of the running app (query parameters, found by reflection on the handlers) and every request DTO that has an enum field.
/// </summary>
public sealed class EnumValidationTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] NamesOf(Type enumType) => Enum.GetNames(enumType);

    private static IEnumerable<string> BadQueryValues(Type enumType)
    {
        var names = NamesOf(enumType);
        yield return "99";
        yield return "-1";
        yield return "0";
        yield return "1";
        yield return "Nope";
        yield return names[0] + "," + names[1];
        yield return names[0] + ", " + names[1];
        yield return names[0] + ",";
        yield return "99," + names[0];
        yield return " " + names[0];
        yield return names[0] + " ";
        yield return "0x" + names.Length;
        yield return "-" + names[0];
    }

    /// <summary>Every GET route of the app that reads an enum from the query string, found from the handlers themselves.</summary>
    private List<(string Route, string Parameter, Type EnumType)> QueryEnumParameters()
    {
        var found = new List<(string, string, Type)>();
        foreach (var endpoint in fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") != true || endpoint.Metadata.GetMetadata<MethodInfo>() is not { } handler)
            {
                continue;
            }

            foreach (var (name, type) in EnumQueryFilter.EnumParameters(handler))
            {
                found.Add((endpoint.RoutePattern.RawText!, name, type));
            }
        }

        return found;
    }

    [Fact]
    public void TheFuzzFindsEveryKnownEnumFilter_SoItCannotQuietlyCoverNothing()
    {
        var found = QueryEnumParameters().Select(p => $"{p.Route}?{p.Parameter}".ToLowerInvariant()).ToHashSet();
        string[] expected =
        [
            "/api/v1/donors?bloodtype", "/api/v1/patients?bloodtype", "/api/v1/appointments?status", "/api/v1/appointments?bloodtype",
            "/api/v1/requests?status", "/api/v1/requests?bloodtype", "/api/v1/receipts?kind", "/api/v1/receipts?bloodtype",
        ];
        Assert.Empty(expected.Except(found));
    }

    [Fact]
    public async Task EveryEnumQueryParameterOfEveryRoute_Refuses_UndefinedNumbersCombinedNamesAndLists_With400_AndAcceptsEachDefinedName()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var failures = new List<string>();
        var parameters = QueryEnumParameters();
        Assert.NotEmpty(parameters);
        foreach (var (route, parameter, enumType) in parameters)
        {
            Assert.DoesNotContain('{', route); // no enum filter sits behind a route parameter today; if one appears this test must learn to fill it
            foreach (var bad in BadQueryValues(enumType))
            {
                using var response = await admin.Client.GetAsync($"{route}?{parameter}={Uri.EscapeDataString(bad)}", Ct);
                var body = await response.Content.ReadAsStringAsync(Ct);
                if (response.StatusCode != HttpStatusCode.BadRequest || !body.Contains(parameter, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"GET {route}?{parameter}={bad} -> {(int)response.StatusCode}");
                }
            }

            // The same name twice is two values where one is expected.
            var first = NamesOf(enumType)[0];
            using var twice = await admin.Client.GetAsync($"{route}?{parameter}={first}&{parameter}={first}", Ct);
            if (twice.StatusCode != HttpStatusCode.BadRequest)
            {
                failures.Add($"GET {route}?{parameter}={first}&{parameter}={first} -> {(int)twice.StatusCode}");
            }

            foreach (var name in NamesOf(enumType))
            {
                using var good = await admin.Client.GetAsync($"{route}?{parameter}={name}", Ct);
                if (good.StatusCode != HttpStatusCode.OK)
                {
                    failures.Add($"GET {route}?{parameter}={name} (a defined name) -> {(int)good.StatusCode}");
                }
            }

            using var empty = await admin.Client.GetAsync($"{route}?{parameter}=", Ct);
            if (empty.StatusCode != HttpStatusCode.BadRequest)
            {
                failures.Add($"GET {route}?{parameter}= (empty) -> {(int)empty.StatusCode}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Verifier_E07_QueryEnumFilters_NumbersAndCombinedNames_Are400_NotAnEmptyPage()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        foreach (var url in new[]
        {
            "/api/v1/donors?bloodType=99", "/api/v1/donors?bloodType=-1", "/api/v1/donors?bloodType=APos,ANeg", "/api/v1/patients?bloodType=99",
            "/api/v1/appointments?status=99", "/api/v1/requests?status=99", "/api/v1/receipts?kind=-1", "/api/v1/receipts?kind=99",
        })
        {
            using var response = await admin.Client.GetAsync(url, Ct);
            using var problem = await response.Problem(400, "validation");
        }
    }

    // ---- bodies ----

    private static Type[] EnumRequestTypes() =>
        [.. typeof(ApiRoutes).Assembly.GetTypes().Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal) && EnumFields(t).Count > 0)];

    private static List<(string Json, Type EnumType)> EnumFields(Type dto)
    {
        var constructor = dto.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var fields = new List<(string, Type)>();
        foreach (var p in constructor.GetParameters())
        {
            var type = p.ParameterType;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>))
            {
                type = type.GetGenericArguments()[0];
            }

            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsEnum)
            {
                fields.Add((JsonNamingPolicy.CamelCase.ConvertName(p.Name!), type));
            }
        }

        return fields;
    }

    public static TheoryData<string> RequestsWithEnums() => new(EnumRequestTypes().Select(t => t.Name));

    /// <summary>Every JSON value that is not exactly one defined name.</summary>
    private static IEnumerable<(string Label, string Json)> BadBodyValues(Type enumType)
    {
        var names = NamesOf(enumType);
        var defined = Enum.GetValues(enumType).Cast<object>().Select(Convert.ToInt32).ToArray();
        yield return ("number 99", "99");
        yield return ("number -1", "-1");
        foreach (var number in defined.Take(2))
        {
            yield return ($"defined number {number}", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        yield return ("numeric string 99", "\"99\"");
        yield return ($"numeric string {defined[0]}", $"\"{defined[0]}\"");
        yield return ("undefined name", "\"Nope\"");
        yield return ("combined names", $"\"{names[0]}, {names[1]}\"");
        yield return ("combined names, no space", $"\"{names[0]},{names[1]}\"");
        yield return ("name and number", $"\"{names[0]}, 1\"");
        yield return ("null", "null");
        yield return ("empty string", "\"\"");
        yield return ("blank string", "\" \"");
        yield return ("padded name", $"\" {names[0]} \"");
        yield return ("true", "true");
        yield return ("number as float", "1.5");
        yield return ("array", "[]");
        yield return ("object", "{}");
    }

    private async Task<(HttpMethod Method, string Path, JsonObject Valid, Func<Task<string>> State)> PrepareAsync(string dto, TestUser admin)
    {
        switch (dto)
        {
            case nameof(CreateDonorRequest):
                return (HttpMethod.Post, "/api/v1/donors", JsonNode.Parse("""{"name":"Fuzz Donor","dateOfBirth":"1990-01-01","gender":"Male","bloodType":"APos","frequency":"Regular"}""")!.AsObject(), () => fx.Db.ScalarAsync<string>("SELECT CONCAT(COUNT(*), ':', COALESCE(SUM(CASE WHEN Gender IN ('Male','Female','Other') THEN 0 ELSE 1 END), 0)) FROM Donor", Ct));
            case nameof(CreatePatientRequest):
                return (HttpMethod.Post, "/api/v1/patients", JsonNode.Parse("""{"name":"Fuzz Patient","dateOfBirth":"1990-01-01","gender":"Male","bloodType":"APos"}""")!.AsObject(), () => fx.Db.ScalarAsync<string>("SELECT CONCAT(COUNT(*), ':', COALESCE(SUM(CASE WHEN Gender IN ('Male','Female','Other') THEN 0 ELSE 1 END), 0)) FROM Patient", Ct));
            case nameof(CreateStaffRequest):
                return (HttpMethod.Post, "/api/v1/staff", JsonNode.Parse($$"""{"displayName":"Fuzz Staff","username":"fuzzstaff","gender":"Male","dateOfBirth":"1990-01-01","designation":"Doctor","shift":"Morning","role":"Staff","salary":1000,"phone":null,"initialPassword":"{{TestSecrets.Initial}}"}""")!.AsObject(), () => fx.Db.ScalarAsync<string>("SELECT CONCAT(COUNT(*), ':', COALESCE(SUM(CASE WHEN Role IN ('Admin','Staff') THEN 0 ELSE 1 END), 0)) FROM Staff", Ct));
            case nameof(UpdateDonorRequest):
            {
                var donor = await Seed.DonorAsync(fx, "Fuzz Update Donor");
                var rv = (await (await admin.Client.GetApi(ApiRoutes.DonorById, donor.Id)).Read<DonorDetail>()).RowVersion;
                return (HttpMethod.Patch, $"/api/v1/donors/{donor.Id}", new JsonObject { ["rowVersion"] = rv }, () => fx.Db.ScalarAsync<string>($"SELECT CONCAT(Gender, ':', BloodType, ':', Frequency, ':', CAST(RowVersion AS bigint)) FROM Donor WHERE Id = {donor.Id}", Ct));
            }

            case nameof(UpdatePatientRequest):
            {
                var patient = await Seed.PatientAsync(fx, "Fuzz Update Patient");
                var rv = (await (await admin.Client.GetApi(ApiRoutes.PatientById, patient.Id)).Read<PatientDetail>()).RowVersion;
                return (HttpMethod.Patch, $"/api/v1/patients/{patient.Id}", new JsonObject { ["rowVersion"] = rv }, () => fx.Db.ScalarAsync<string>($"SELECT CONCAT(Gender, ':', BloodType, ':', CAST(RowVersion AS bigint)) FROM Patient WHERE Id = {patient.Id}", Ct));
            }

            case nameof(UpdateStaffRequest):
            {
                var worker = await fx.AddStaffAsync(Role.Staff);
                var rv = (await (await admin.Client.GetApi(ApiRoutes.StaffById, worker.Id)).Read<StaffDetail>()).RowVersion;
                return (HttpMethod.Patch, $"/api/v1/staff/{worker.Id}", new JsonObject { ["rowVersion"] = rv }, () => fx.Db.ScalarAsync<string>($"SELECT CONCAT(Gender, ':', Designation, ':', Shift, ':', Role, ':', CAST(RowVersion AS bigint)) FROM Staff WHERE Id = {worker.Id}", Ct));
            }

            default:
                throw new InvalidOperationException($"{dto} has an enum field but this test does not know which route takes it. Add it here so its enum fields are fuzzed.");
        }
    }

    [Theory]
    [MemberData(nameof(RequestsWithEnums))]
    public async Task EveryEnumFieldOfEveryRequestDto_RefusesAnythingButOneDefinedName_With400_AndStoresNothing(string dto)
    {
        var type = EnumRequestTypes().Single(t => t.Name == dto);
        var admin = await fx.AddStaffAsync(Role.Admin);
        var failures = new List<string>();
        var rounds = 0;
        foreach (var (field, enumType) in EnumFields(type))
        {
            // Each field gets a fresh target for the update DTOs, so one field's checks cannot disturb another's.
            var (method, path, valid, state) = await PrepareAsync(dto, admin);
            var before = await state();

            foreach (var (label, json) in BadBodyValues(enumType))
            {
                var body = (JsonObject)valid.DeepClone();
                body[field] = JsonNode.Parse(json);
                using var response = await Send(admin.Client, method, path, body.ToJsonString());
                var text = await response.Content.ReadAsStringAsync(Ct);
                rounds++;
                if (response.StatusCode != HttpStatusCode.BadRequest || !text.Contains(field, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{dto}.{field} = {label} ({json}) -> {(int)response.StatusCode} {Trim(text)}");
                }
            }

            if (before != await state())
            {
                failures.Add($"{dto}.{field}: a refused body changed the database ({before} -> {await state()})");
            }

            // The control: every enum a defined name (lower case too), accepted.
            foreach (var spelling in method == HttpMethod.Post ? [NamesOf(enumType)[^1], NamesOf(enumType)[0].ToLowerInvariant()] : new[] { NamesOf(enumType)[0].ToLowerInvariant() })
            {
                var control = (JsonObject)valid.DeepClone();
                control[field] = spelling;
                if (method == HttpMethod.Post && control.ContainsKey("username"))
                {
                    control["username"] = "fuzz" + Guid.NewGuid().ToString("N")[..12];
                }

                using var ok = await Send(admin.Client, method, path, control.ToJsonString());
                if (!ok.IsSuccessStatusCode)
                {
                    failures.Add($"{dto}.{field} = {spelling} (a defined name) -> {(int)ok.StatusCode} {Trim(await ok.Content.ReadAsStringAsync(Ct))}");
                }
            }
        }

        Assert.True(rounds >= 15, "the fuzz ran almost nothing");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Verifier_E17_CombinedGenderNames_AreRefused_NotStoredAsFemale()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var before = await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor", Ct);
        using var response = await admin.Client.PostRaw(ApiRoutes.Donors, """{"name":"x y","dateOfBirth":"1990-01-01","gender":"Male, Female","bloodType":"APos","frequency":"Regular"}""");
        using var problem = await response.Problem(400, "validation");
        Assert.Equal(before, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor", Ct));
    }

    [Fact]
    public async Task APatchThatSendsNullForADropdown_IsRefused_NotTurnedIntoTheFirstValue()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var donor = await Seed.DonorAsync(fx, "Null Dropdown Donor", gender: Gender.Female);
        var rv = (await (await admin.Client.GetApi(ApiRoutes.DonorById, donor.Id)).Read<DonorDetail>()).RowVersion;

        using var response = await admin.Client.PatchRaw(ApiRoutes.DonorById, donor.Id, $$"""{"rowVersion":"{{rv}}","gender":null}""");

        using var problem = await response.Problem(400);
        Assert.Equal("Female", await fx.Db.ScalarAsync<string>($"SELECT Gender FROM Donor WHERE Id = {donor.Id}", Ct));
    }

    [Fact]
    public void TheStrictConverter_RoundTripsNames_AndNothingElse()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new StrictEnumConverterFactory());
        Assert.Equal("\"OPos\"", JsonSerializer.Serialize(BloodType.OPos, options));
        Assert.Equal(BloodType.ONeg, JsonSerializer.Deserialize<BloodType>("\"oneg\"", options));
        Assert.Null(JsonSerializer.Deserialize<BloodType?>("null", options));
        foreach (var bad in new[] { "3", "\"3\"", "\"APos, ANeg\"", "\"\"", "true", "[]" })
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BloodType>(bad, options));
        }
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string json) =>
        client.SendAsync(new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, Ct);

    private static string Trim(string text) => text.Length > 200 ? text[..200] : text;
}
