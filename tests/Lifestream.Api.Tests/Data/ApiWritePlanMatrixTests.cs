using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests.Data;

/// <summary>Records every command the API sends (queries, writes and scalars), with a copy of each parameter, while <see cref="Recording"/> is true.</summary>
public sealed class AllStatementCapture : DbCommandInterceptor
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Sql, List<SqlParameter> Parameters)> _statements = new();

    public bool Recording { get; set; }

    public List<(string Sql, List<SqlParameter> Parameters)> Drain()
    {
        var list = new List<(string, List<SqlParameter>)>();
        while (_statements.TryDequeue(out var item))
        {
            list.Add(item);
        }

        return list;
    }

    private void Add(DbCommand command)
    {
        if (Recording)
        {
            _statements.Enqueue((command.CommandText, [.. command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone())]));
        }
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Add(command);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Add(command);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Add(command);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>The 100x database plus the API on top of it, recording every statement the API sends (reads AND writes).</summary>
public sealed class WritePlanApiFixture : ApiFixture
{
    public AllStatementCapture All { get; } = new();

    protected override SqlServerDatabaseFixture CreateDb() => new ScaleDatabaseFixture();

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<IInterceptor>(All);
}

/// <summary>
/// DB-05 for the WRITE paths and the detail endpoints, through the real API on a 100x database on a real SQL Server. Every write endpoint
/// (create, update, delete, book, reschedule, cancel, record donation, fulfil, cancel request, photo upload, login, demo login, refresh,
/// logout, password change) and every detail endpoint is called over HTTP; an interceptor records each statement the API sent, and the plan
/// of each (the plan SQL Server compiled and cached for it, read from sys.dm_exec_cached_plans) must contain no Table Scan or Clustered Index Scan on a table
/// with more than 5,000 rows. The fulfil statement is additionally replayed with its actual plan inside a transaction that is rolled back.
/// Set LIFESTREAM_PLAN_CAPTURE to a file path to write one line per statement.
/// </summary>
[Collection("Scale datasets: one class at a time")]
public sealed partial class ApiWritePlanMatrixTests(WritePlanApiFixture fx) : IClassFixture<WritePlanApiFixture>
{
    private const int BigTableRows = 5_000;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Log(string line) => TestContext.Current.TestOutputHelper!.WriteLine(line);

    private async Task<Dictionary<string, long>> SizesAsync() =>
        (await fx.Db.RowsAsync("SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) GROUP BY t.name", Ct))
            .ToDictionary(r => (string)r[0]!, r => Convert.ToInt64(r[1], CultureInfo.InvariantCulture));

    /// <summary>
    /// The plans SQL Server compiled and cached for the exact statement text the API sent (sys.dm_exec_cached_plans, matched on the text, so no
    /// statement is run a second time and nothing is guessed from timing). A statement run with a parameter list (sp_executesql) is cached
    /// with the sniffed values of the call, so these are the plans the API's calls really used.
    /// </summary>
    private async Task<List<XDocument>> CachedPlansAsync(string sql)
    {
        await using var connection = new SqlConnection(fx.Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(
            """
            -- Only this database's plans are opened: reading the text or plan of another test database that is being dropped fails with "in transition".
            SELECT cp.plan_handle INTO #mine
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) pa
            WHERE pa.attribute = 'dbid' AND CONVERT(int, pa.value) = DB_ID();

            SELECT qp.query_plan
            FROM #mine m
            CROSS APPLY sys.dm_exec_sql_text(m.plan_handle) st
            CROSS APPLY sys.dm_exec_query_plan(m.plan_handle) qp
            WHERE CHARINDEX(@sql, st.text) > 0 AND qp.query_plan IS NOT NULL;
            """,
            connection);
        command.Parameters.Add(new SqlParameter("@sql", System.Data.SqlDbType.NVarChar, -1) { Value = sql });
        var plans = new List<XDocument>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            plans.Add(XDocument.Parse(reader.GetString(0)));
        }

        return plans;
    }

    /// <summary>The actual plans of one statement: it is run inside a transaction that is rolled back, so the data does not change.</summary>
    private async Task<List<XDocument>> ActualPlansRolledBackAsync(string sql, List<SqlParameter> parameters)
    {
        await using var connection = new SqlConnection(fx.Db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
        await using (var set = new SqlCommand("SET STATISTICS XML ON;", connection, tx))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        var plans = new List<XDocument>();
        await using (var command = new SqlCommand(sql, connection, tx))
        {
            command.Parameters.AddRange(parameters.Select(p => (SqlParameter)((ICloneable)p).Clone()).ToArray());
            await using var reader = await command.ExecuteReaderAsync(Ct);
            do
            {
                while (await reader.ReadAsync(Ct))
                {
                    if (reader.FieldCount == 1 && reader.GetName(0).Contains("XML Showplan", StringComparison.Ordinal))
                    {
                        plans.Add(XDocument.Parse(reader.GetString(0)));
                    }
                }
            }
            while (await reader.NextResultAsync(Ct));
        }

        await tx.RollbackAsync(Ct);
        return plans;
    }

    private static IEnumerable<(string Physical, string? Table, string? Index, long Rows)> Operators(XDocument plan) =>
        plan.Descendants(Ns + "RelOp").Select(op =>
        {
            var obj = op.Elements().SelectMany(e => e.Elements(Ns + "Object")).FirstOrDefault();
            var rows = op.Elements(Ns + "RunTimeInformation").Elements(Ns + "RunTimeCountersPerThread").Sum(c => (long?)c.Attribute("ActualRows") ?? 0);
            return ((string)op.Attribute("PhysicalOp")!, ((string?)obj?.Attribute("Table"))?.Trim('[', ']'), ((string?)obj?.Attribute("Index"))?.Trim('[', ']'), rows);
        });

    private static List<string> BigScans(IEnumerable<XDocument> plans, Dictionary<string, long> sizes) =>
        plans.SelectMany(Operators)
            .Where(o => o.Physical is "Table Scan" or "Clustered Index Scan" && o.Table is not null && sizes.TryGetValue(o.Table, out var n) && n > BigTableRows)
            .Select(o => $"{o.Physical}:{o.Table}.{o.Index}")
            .ToList();

    private static string Describe(IEnumerable<XDocument> plans) =>
        string.Join(" ; ", plans.SelectMany(Operators).Where(o => o.Physical.Contains("Scan", StringComparison.Ordinal) || o.Physical.Contains("Seek", StringComparison.Ordinal)).Select(o => $"{o.Physical}:{o.Table}.{o.Index}" + (o.Rows > 0 ? $"[rows={o.Rows}]" : "")));

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteVerb();

    private static readonly object Gate = new();

    private static void Record(string op, string sql, string plan)
    {
        var path = Environment.GetEnvironmentVariable("LIFESTREAM_PLAN_CAPTURE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Gate)
        {
            File.AppendAllText(path, $"OP {op}\n   SQL: {sql.ReplaceLineEndings(" ")}\n   PLAN: {plan}\n");
        }
    }

    /// <summary>Every route the API maps for a write or a detail read (read from the endpoint source, so a new endpoint cannot be forgotten).</summary>
    private static (HashSet<string> Writes, HashSet<string> Details) MappedRoutes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Lifestream.Api", "Features")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var writes = new HashSet<string>();
        var details = new HashSet<string>();
        var constants = typeof(ApiRoutes).GetFields().Where(f => f.IsLiteral).ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        foreach (var file in Directory.GetFiles(Path.Combine(dir.FullName, "src", "Lifestream.Api", "Features"), "*.cs"))
        {
            foreach (Match m in MapRegex().Matches(File.ReadAllText(file)))
            {
                var route = constants[m.Groups[2].Value];
                if (m.Groups[1].Value == "Get")
                {
                    if (route.Contains("{id}", StringComparison.Ordinal))
                    {
                        details.Add(route);
                    }
                }
                else
                {
                    writes.Add(route);
                }
            }
        }

        return (writes, details);
    }

    [GeneratedRegex(@"Map(Get|Post|Put|Patch|Delete)\(ApiRoutes\.(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex MapRegex();

    [Fact]
    public async Task RanAgainstTheScaleDataset_OnARealSqlServer_ThroughTheApi()
    {
        Assert.Contains("Microsoft SQL Server", fx.Db.ServerVersion, StringComparison.Ordinal);
        // The other tests of this class add a few rows, so the counts are "at least the 100x seed".
        Assert.True(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt", Ct) >= 27_000);
        Assert.True(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Request", Ct) >= 15_000);
        Assert.True(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM BloodBag", Ct) > BigTableRows);
        Assert.True(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM BloodBag WHERE Status = 'Available' AND BloodType = 'OPos'", Ct) > 1_000);
    }

    /// <summary>
    /// The statement the API sends to issue blood (the oldest unexpired available bags of a type) during a real fulfil call, at 100x, for common and
    /// rare types and one and ten units, with its actual plan: it must not scan the BloodBag table.
    /// </summary>
    [Fact]
    public async Task Fulfil_TheActualIssueStatementTheApiSends_AtScale_DoesNotScanBloodBag()
    {
        var sizes = await SizesAsync();
        Assert.True(sizes["BloodBag"] > BigTableRows);
        var admin = await fx.AddStaffAsync(Role.Admin, "fulfilplanadmin");
        (await admin.Client.GetAsync(ApiRoutes.Path(ApiRoutes.Me), Ct)).Dispose();

        var problems = new List<string>();
        var issued = 0;
        foreach (var type in new[] { BloodType.OPos, BloodType.APos, BloodType.ONeg, BloodType.BPos, BloodType.ANeg, BloodType.ABNeg })
        {
            foreach (var units in new[] { 1, 10 })
            {
                var patient = await (await admin.Client.PostApi(ApiRoutes.Patients, new CreatePatientRequest(fx.NextName("Fulfil Patient "), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-40)), Gender.Female, type, null, null))).Read<PatientDetail>();
                var request = await (await admin.Client.PostApi(ApiRoutes.Requests, new CreateRequestRequest(patient.Id, units, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5))))).Read<RequestDetail>();

                fx.All.Drain();
                fx.All.Recording = true;
                HttpResponseMessage response;
                try
                {
                    response = await admin.Client.PostApi(ApiRoutes.RequestFulfil, null, request.Id);
                }
                finally
                {
                    fx.All.Recording = false;
                }

                var body = await response.Content.ReadAsStringAsync(Ct);
                if (type is BloodType.OPos or BloodType.APos or BloodType.ONeg or BloodType.BPos)
                {
                    Assert.True(response.IsSuccessStatusCode, $"fulfil {type} x{units}: {(int)response.StatusCode} {body}");
                }

                var statements = fx.All.Drain();
                var issue = statements.Where(s => s.Sql.Contains("UPDATE [BloodBag]", StringComparison.Ordinal)).ToList();
                Assert.True(issue.Count == 1, $"fulfil {type} x{units}: {issue.Count} statements update BloodBag; the API sent:\n{string.Join("\n---\n", statements.Select(s => s.Sql))}");
                if (response.IsSuccessStatusCode)
                {
                    issued++;
                }

                // The API's own receipt may be gone (a fulfil that found too little stock rolls back), so the replay points at an existing issue receipt.
                var replay = issue[0].Parameters.Select(p => (SqlParameter)((ICloneable)p).Clone()).ToList();
                replay.Single(p => p.ParameterName == "@receipt").Value = await fx.Db.ScalarAsync<int>("SELECT MAX(Id) FROM Receipt WHERE Kind = 'Issue'", Ct);
                var plans = await ActualPlansRolledBackAsync(issue[0].Sql, replay);
                Assert.NotEmpty(plans);
                var scans = BigScans(plans, sizes);
                var line = $"fulfil {type} x{units} status={(int)response.StatusCode}: {Describe(plans)}";
                Log(line);
                Record(line, issue[0].Sql, Describe(plans));
                if (scans.Count > 0)
                {
                    problems.Add($"{line} => scans {string.Join(", ", scans)}");
                }
            }
        }

        Assert.True(issued >= 8, $"only {issued} fulfil calls succeeded");
        Assert.True(problems.Count == 0, "The issue statement scans BloodBag (a table over 5,000 rows):\n" + string.Join("\n", problems));
    }

    [Fact]
    public async Task EveryWriteAndDetailEndpoint_AtScale_SendsNoStatementThatScansABigTable()
    {
        var sizes = await SizesAsync();
        var admin = await fx.AddStaffAsync(Role.Admin, "writeplanadmin");
        var c = admin.Client;
        (await c.GetAsync(ApiRoutes.Path(ApiRoutes.Me), Ct)).Dispose();

        var covered = new HashSet<string>();
        var violations = new List<string>();
        var statementCount = 0;

        async Task Run(string route, string op, Func<Task<HttpResponseMessage>> call, bool expectWrite, params HttpStatusCode[] accepted)
        {
            fx.All.Drain();
            fx.All.Recording = true;
            HttpResponseMessage response;
            try
            {
                response = await call();
            }
            finally
            {
                fx.All.Recording = false;
            }

            var statements = fx.All.Drain();
            var body = await response.Content.ReadAsStringAsync(Ct);
            var ok = accepted.Length == 0 ? response.IsSuccessStatusCode : accepted.Contains(response.StatusCode);
            Assert.True(ok, $"{op}: {(int)response.StatusCode} {body}");
            covered.Add(route);
            if (expectWrite)
            {
                Assert.True(statements.Any(s => WriteVerb().IsMatch(s.Sql)), $"{op}: no INSERT/UPDATE/DELETE reached the database, so this call did not exercise a write path");
            }

            foreach (var (sql, _) in statements)
            {
                statementCount++;
                var plans = await CachedPlansAsync(sql);
                Assert.True(plans.Count > 0, $"{op}: no plan for: {sql}");
                var scans = BigScans(plans, sizes);
                Record(op, sql, Describe(plans));
                if (scans.Count > 0)
                {
                    violations.Add($"{op} [{(int)response.StatusCode}]: {string.Join(", ", scans)} IN {sql.ReplaceLineEndings(" ")[..Math.Min(300, sql.Length)]}");
                }
            }

            Log($"OP {op} status={(int)response.StatusCode} statements={statements.Count}");
            response.Dispose();
        }

        async Task<DateTime> FreeSlotAsync(int daysAhead)
        {
            for (var d = daysAhead; d < daysAhead + 60; d++)
            {
                var day = DateTime.UtcNow.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var slots = await (await c.GetAsync(ApiRoutes.Path(ApiRoutes.AppointmentSlots) + "?date=" + day, Ct)).Read<List<SlotInfo>>();
                if (slots.FirstOrDefault(x => !x.IsTaken) is { } free)
                {
                    return free.StartUtc;
                }
            }

            throw new InvalidOperationException("no free slot");
        }

        var someDonor = await fx.Db.ScalarAsync<int>("SELECT TOP 1 DonorId FROM Appointment GROUP BY DonorId ORDER BY COUNT(*) DESC", Ct);
        var someAppointmentWithReceipt = await fx.Db.ScalarAsync<int>("SELECT TOP 1 AppointmentId FROM Receipt WHERE AppointmentId IS NOT NULL ORDER BY Id DESC", Ct);
        var someRequest = await fx.Db.ScalarAsync<int>("SELECT TOP 1 RequestId FROM Receipt WHERE RequestId IS NOT NULL ORDER BY Id DESC", Ct);
        var somePatient = await fx.Db.ScalarAsync<int>("SELECT TOP 1 PatientId FROM Request GROUP BY PatientId ORDER BY COUNT(*) DESC", Ct);
        var someReceipt = await fx.Db.ScalarAsync<int>("SELECT MAX(Id) FROM Receipt", Ct);
        var someStaff = await fx.Db.ScalarAsync<int>("SELECT TOP 1 StaffId FROM Receipt GROUP BY StaffId ORDER BY COUNT(*) DESC", Ct);

        // Detail reads.
        await Run(ApiRoutes.DonorById, "GET donor detail", () => c.GetApi(ApiRoutes.DonorById, someDonor), false);
        await Run(ApiRoutes.DonorEligibility, "GET donor eligibility", () => c.GetApi(ApiRoutes.DonorEligibility, someDonor), false);
        await Run(ApiRoutes.PatientById, "GET patient detail", () => c.GetApi(ApiRoutes.PatientById, somePatient), false);
        await Run(ApiRoutes.AppointmentById, "GET appointment detail (with receipt)", () => c.GetApi(ApiRoutes.AppointmentById, someAppointmentWithReceipt), false);
        await Run(ApiRoutes.RequestById, "GET request detail (fulfilled)", () => c.GetApi(ApiRoutes.RequestById, someRequest), false);
        await Run(ApiRoutes.ReceiptById, "GET receipt detail", () => c.GetApi(ApiRoutes.ReceiptById, someReceipt), false);
        await Run(ApiRoutes.StaffById, "GET staff detail", () => c.GetApi(ApiRoutes.StaffById, someStaff), false);
        await Run(ApiRoutes.StaffPhoto, "GET staff photo", () => c.GetApi(ApiRoutes.StaffPhoto, someStaff), false, HttpStatusCode.OK, HttpStatusCode.NotFound);

        // Donors.
        DonorDetail donor = null!;
        await Run(ApiRoutes.Donors, "POST donor", async () =>
        {
            var r = await c.PostApi(ApiRoutes.Donors, new CreateDonorRequest(fx.NextName("Plan Donor "), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30)), Gender.Male, BloodType.APos, "+1-555-0199", null, DonorFrequency.Regular));
            donor = await r.Read<DonorDetail>();
            return r;
        }, true);
        await Run(ApiRoutes.DonorById, "PATCH donor phone", async () =>
        {
            var r = await c.PatchApi(ApiRoutes.DonorById, donor.Id, new UpdateDonorRequest(donor.RowVersion) { Phone = new Optional<string?>(true, "+1-555-0198") });
            donor = await r.Read<DonorDetail>();
            return r;
        }, true);
        var busy = await (await c.GetApi(ApiRoutes.DonorById, someDonor)).Read<DonorDetail>();
        var newType = busy.BloodType == BloodType.ONeg ? BloodType.OPos : BloodType.ONeg;
        await Run(ApiRoutes.DonorById, "PATCH donor blood type (the donor with the most appointments)", () => c.PatchApi(ApiRoutes.DonorById, someDonor, new UpdateDonorRequest(busy.RowVersion) { BloodType = new Optional<BloodType>(true, newType) }), true);
        await Run(ApiRoutes.DonorById, "DELETE donor with appointments (refused)", () => c.DeleteApi(ApiRoutes.DonorById, someDonor), false, HttpStatusCode.Conflict);

        // Appointments.
        AppointmentDetail booked = null!;
        await Run(ApiRoutes.Appointments, "POST appointment (book)", async () =>
        {
            var r = await c.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, await FreeSlotAsync(15), null));
            booked = await r.Read<AppointmentDetail>();
            return r;
        }, true);
        await Run(ApiRoutes.AppointmentById, "PATCH appointment (reschedule)", async () =>
        {
            var r = await c.PatchApi(ApiRoutes.AppointmentById, booked.Id, new RescheduleAppointmentRequest(booked.RowVersion, await FreeSlotAsync(16)));
            booked = await r.Read<AppointmentDetail>();
            return r;
        }, true);
        await Run(ApiRoutes.AppointmentDonation, "POST appointment donation (record)", () => c.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(2, null), booked.Id), true);

        // The donor donated today, so a second donation today is judged ineligible and needs an override reason (stored on the appointment).
        var second = await (await c.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, await FreeSlotAsync(170), null))).Read<AppointmentDetail>();
        await Run(ApiRoutes.AppointmentDonation, "POST appointment donation (override reason)", () => c.PostApi(ApiRoutes.AppointmentDonation, new RecordDonationRequest(1, null) { OverrideReason = "Plan matrix: urgent shortage" }, second.Id), true);
        var toCancel = await (await c.PostApi(ApiRoutes.Appointments, new BookAppointmentRequest(donor.Id, await FreeSlotAsync(150), "plan"))).Read<AppointmentDetail>();
        await Run(ApiRoutes.AppointmentCancel, "POST appointment cancel", () => c.PostApi(ApiRoutes.AppointmentCancel, null, toCancel.Id), true);
        await Run(ApiRoutes.AppointmentById, "DELETE appointment", () => c.DeleteApi(ApiRoutes.AppointmentById, toCancel.Id), true);

        // Patients and requests.
        PatientDetail patient = null!;
        await Run(ApiRoutes.Patients, "POST patient", async () =>
        {
            var r = await c.PostApi(ApiRoutes.Patients, new CreatePatientRequest(fx.NextName("Plan Patient "), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-40)), Gender.Female, BloodType.OPos, null, null));
            patient = await r.Read<PatientDetail>();
            return r;
        }, true);
        await Run(ApiRoutes.PatientById, "PATCH patient", () => c.PatchApi(ApiRoutes.PatientById, patient.Id, new UpdatePatientRequest(patient.RowVersion) { Address = new Optional<string?>(true, "2 Plan Road") }), true);
        RequestDetail request = null!;
        await Run(ApiRoutes.Requests, "POST request", async () =>
        {
            var r = await c.PostApi(ApiRoutes.Requests, new CreateRequestRequest(patient.Id, 2, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5))));
            request = await r.Read<RequestDetail>();
            return r;
        }, true);
        await Run(ApiRoutes.RequestById, "PATCH request", () => c.PatchApi(ApiRoutes.RequestById, request.Id, new UpdateRequestRequest(request.RowVersion) { Units = new Optional<int>(true, 1) }), true);
        await Run(ApiRoutes.RequestFulfil, "POST request fulfil (OPos at 100x stock)", () => c.PostApi(ApiRoutes.RequestFulfil, null, request.Id), true);
        var toDelete = await (await c.PostApi(ApiRoutes.Requests, new CreateRequestRequest(patient.Id, 10, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5))))).Read<RequestDetail>();
        var toCancelRequest = await (await c.PostApi(ApiRoutes.Requests, new CreateRequestRequest(patient.Id, 1, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5))))).Read<RequestDetail>();
        await Run(ApiRoutes.RequestCancel, "POST request cancel", () => c.PostApi(ApiRoutes.RequestCancel, null, toCancelRequest.Id), true);
        await Run(ApiRoutes.RequestById, "DELETE request", () => c.DeleteApi(ApiRoutes.RequestById, toDelete.Id), true);
        await Run(ApiRoutes.PatientById, "DELETE patient with requests (refused)", () => c.DeleteApi(ApiRoutes.PatientById, patient.Id), false, HttpStatusCode.Conflict);
        await Run(ApiRoutes.DonorById, "DELETE donor with a receipt (refused)", () => c.DeleteApi(ApiRoutes.DonorById, donor.Id), false, HttpStatusCode.Conflict);
        var lonelyDonor = await (await c.PostApi(ApiRoutes.Donors, new CreateDonorRequest(fx.NextName("Plan Donor "), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30)), Gender.Male, BloodType.APos, null, null, DonorFrequency.Regular))).Read<DonorDetail>();
        await Run(ApiRoutes.DonorById, "DELETE donor (no children)", () => c.DeleteApi(ApiRoutes.DonorById, lonelyDonor.Id), true);
        await Run(ApiRoutes.DonorRestore, "POST donor restore", () => c.PostApi(ApiRoutes.DonorRestore, null, lonelyDonor.Id), true);
        var lonelyPatient = await (await c.PostApi(ApiRoutes.Patients, new CreatePatientRequest(fx.NextName("Plan Patient "), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-40)), Gender.Female, BloodType.OPos, null, null))).Read<PatientDetail>();
        await Run(ApiRoutes.PatientById, "DELETE patient (no children)", () => c.DeleteApi(ApiRoutes.PatientById, lonelyPatient.Id), true);
        await Run(ApiRoutes.PatientRestore, "POST patient restore", () => c.PostApi(ApiRoutes.PatientRestore, null, lonelyPatient.Id), true);

        // Staff, photos and the profile.
        StaffDetail staff = null!;
        var username = "planstaff" + Guid.NewGuid().ToString("N")[..6];
        await Run(ApiRoutes.Staff, "POST staff", async () =>
        {
            var r = await c.PostApi(ApiRoutes.Staff, new CreateStaffRequest("Plan Staff", username, Gender.Male, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30)), Designation.Worker, Shift.Morning, Role.Staff, 1000, null, "Verify-Passw0rd-123!"));
            staff = await r.Read<StaffDetail>();
            return r;
        }, true);
        await Run(ApiRoutes.StaffById, "PATCH staff", () => c.PatchApi(ApiRoutes.StaffById, staff.Id, new UpdateStaffRequest(staff.RowVersion) { Salary = new Optional<int?>(true, 1100) }), true);
        await Run(ApiRoutes.StaffResetPassword, "POST staff reset password", () => c.PostApi(ApiRoutes.StaffResetPassword, new ResetPasswordRequest("Another-Temp-Passw0rd-456!"), staff.Id), true);
        var png = TestImages.Png(40, 40);
        await Run(ApiRoutes.StaffPhoto, "PUT staff photo (upload)", () => c.PutBytes(ApiRoutes.StaffPhoto, staff.Id, png, "image/png"), true);
        await Run(ApiRoutes.StaffPhoto, "GET staff photo (stored)", () => c.GetApi(ApiRoutes.StaffPhoto, staff.Id), false);
        await Run(ApiRoutes.StaffById, "DELETE staff with receipts (refused)", () => c.DeleteApi(ApiRoutes.StaffById, someStaff), false, HttpStatusCode.Conflict);
        await Run(ApiRoutes.StaffById, "DELETE staff", () => c.DeleteApi(ApiRoutes.StaffById, staff.Id), true);

        var me = await fx.AddStaffAsync(Role.Staff, "planme07");
        var mine = await (await me.Client.GetAsync(ApiRoutes.Path(ApiRoutes.Me), Ct)).Read<ProfileResponse>();
        await Run(ApiRoutes.Me, "PATCH me", () => me.Client.PatchMe(new UpdateProfileRequest(mine.RowVersion) { DisplayName = new Optional<string>(true, "Plan Me Renamed") }), true);
        await Run(ApiRoutes.MePhoto, "PUT me photo", () => me.Client.PutBytes(ApiRoutes.MePhoto, null, png, "image/png"), true);
        await Run(ApiRoutes.MePassword, "PUT me password", () => me.Client.PutApi(ApiRoutes.MePassword, new ChangePasswordRequest(ApiFixture.Password, "a-brand-new-password-1")), true);

        // Auth.
        var loginUser = await fx.AddStaffAsync(Role.Staff, "planlogin07");
        TokenResponse token = null!;
        await Run(ApiRoutes.AuthLogin, "POST login (good)", async () =>
        {
            var r = await fx.NewClient().PostApi(ApiRoutes.AuthLogin, new LoginRequest(loginUser.Username, ApiFixture.Password));
            token = await r.Read<TokenResponse>();
            return r;
        }, true);
        await Run(ApiRoutes.AuthLogin, "POST login (wrong password)", () => fx.NewClient().PostApi(ApiRoutes.AuthLogin, new LoginRequest(loginUser.Username, "wrong-password-x")), true, HttpStatusCode.Unauthorized);
        await Run(ApiRoutes.AuthRefresh, "POST refresh", async () =>
        {
            var r = await fx.NewClient().PostApi(ApiRoutes.AuthRefresh, new RefreshRequest(token.RefreshToken));
            token = await r.Read<TokenResponse>();
            return r;
        }, true);
        await Run(ApiRoutes.AuthLogout, "POST logout", () => fx.NewClient().PostApi(ApiRoutes.AuthLogout, new RefreshRequest(token.RefreshToken)), true, HttpStatusCode.NoContent);
        await fx.MarkResetTodayAsync();
        await Run(ApiRoutes.AuthDemo, "POST demo login", () => fx.NewClient().PostApi(ApiRoutes.AuthDemo), false); // the demo login only reads the demo staff row and mints a token

        // Every write and detail endpoint the API maps was driven (DemoReset is excluded: it wipes the dataset).
        var (writes, details) = MappedRoutes();
        writes.Remove(ApiRoutes.DemoReset);
        var missing = writes.Concat(details).Where(r => !covered.Contains(r)).ToList();
        Assert.True(missing.Count == 0, "Endpoints this matrix did not call: " + string.Join(", ", missing));
        Log($"STATEMENTS {statementCount} across {covered.Count} routes");

        Assert.True(violations.Count == 0, "Statements the API sent that scan a table over 5,000 rows:\n  " + string.Join("\n  ", violations));
    }
}
