using System.Text.Json;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// OWN-07: the perf harness must be unable to report success when a measurement is missing. These run in the default gate (no API, no fixture database): they exercise
/// the recording code of <see cref="PerfTests"/> directly, with a skeleton that never showed.
/// </summary>
public sealed class PerfHarnessSelfTests
{
    private static List<JsonElement> RecordTo(string file, Action record)
    {
        PerfLog.PathOverride = file;
        try
        {
            record();
        }
        finally
        {
            PerfLog.PathOverride = null;
        }

        return [.. File.ReadAllLines(file).Where(l => l.Length > 0).Select(l => JsonElement.Parse(l))];
    }

    [Fact]
    public void MissingReceiptsSkeleton_IsRecordedAsMissed_NeverInfinity_AndEveryLaterScreenStillGetsARecord()
    {
        var file = Path.Combine(Path.GetTempPath(), "perf-selftest-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            string[] screens = ["Dashboard", "Donors", "Receipts", "Staff", "Profile"];
            var problems = new List<string>();
            var rows = RecordTo(file, () =>
            {
                foreach (var screen in screens)
                {
                    // Receipts: its skeleton was never visible on three of ten visits.
                    IReadOnlyList<double?> times = screen == "Receipts"
                        ? [12.0, null, 14.0, null, 9.0, 8.0, 7.0, 6.0, null, 5.0]
                        : [12.0, 11.0, 14.0, 9.0, 8.0, 7.0, 6.0, 5.0, 4.0, 3.0];
                    if (!PerfTests.RecordSkeleton(screen, 10, times, out var problem))
                    {
                        problems.Add(problem);
                    }
                }
            });

            Assert.Equal(screens.Length, rows.Count);
            Assert.Equal(screens.Select(PerfTests.SkeletonKey), rows.Select(r => r.GetProperty("key").GetString()));
            var receipts = rows.Single(r => r.GetProperty("key").GetString() == "skeleton receipts");
            Assert.False(receipts.GetProperty("pass").GetBoolean());
            Assert.True(receipts.GetProperty("missed").GetBoolean());
            Assert.Equal(0.0, receipts.GetProperty("value").GetDouble());
            Assert.Contains("MISSED", receipts.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.All(rows.Where(r => r.GetProperty("key").GetString() != "skeleton receipts"), r => Assert.True(r.GetProperty("pass").GetBoolean()));
            Assert.Equal(["MISSED skeleton receipts: no skeleton was visible after navigating to Receipts on 3 of 10 visits"], problems);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void RecordingANonFiniteValue_WritesAMissedRow_InsteadOfThrowing(double value)
    {
        var file = Path.Combine(Path.GetTempPath(), "perf-selftest-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var passed = true;
            var rows = RecordTo(file, () => passed = PerfLog.Record("ui-gap", "UI thread gap while loading Stock", "one load", 1, value, "ms", "< 50", true, key: "ui-gap stock"));

            Assert.False(passed);
            var row = Assert.Single(rows);
            Assert.True(row.GetProperty("missed").GetBoolean());
            Assert.False(row.GetProperty("pass").GetBoolean());
            Assert.Equal("ui-gap stock", row.GetProperty("key").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ExpectDeclaresTheRowsATestWillRecord()
    {
        var file = Path.Combine(Path.GetTempPath(), "perf-selftest-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var rows = RecordTo(file, () => PerfLog.Expect("skeleton dashboard", "skeleton donors"));

            var row = Assert.Single(rows);
            Assert.Equal(["skeleton dashboard", "skeleton donors"], row.GetProperty("expect").EnumerateArray().Select(e => e.GetString()));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
