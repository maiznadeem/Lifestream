namespace Lifestream.Api.Tests.Data;

/// <summary>
/// The kept 100x template is only as right as its key: every change that can change the database must change the key, or a run would test against an
/// old database. These tests change one thing at a time in a small copy of the layout and in the real sources' list.
/// </summary>
public sealed class ScaleTemplateKeyTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 11, 20);
    private const string Server = "17.0.1000.7 SQL_Latin1_General_CP1_CI_AS";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lifestream-template-key-" + Guid.NewGuid().ToString("N"));

    public ScaleTemplateKeyTests()
    {
        Write("src/Lifestream.Data/Seed/Generators.cs", "class Generators\n{\n    const int DonorsPerFactor = 40;\n}\n");
        Write("src/Lifestream.Data/Seed/SyntheticDataBuilder.cs", "class SyntheticDataBuilder { }\n");
        Write("src/Lifestream.Data/Migrations/20260101000000_Initial.cs", "class Initial { }\n");
        Write("src/Lifestream.Data/Lifestream.Data.csproj", "<Project />\n");
        Write("src/Lifestream.Domain/BloodType.cs", "enum BloodType { APos }\n");
        Write("Directory.Packages.props", "<Project />\n");
        Write("tests/Lifestream.Api.Tests/Data/SqlServerDatabaseFixture.cs", "class Fixture { }\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string text)
    {
        var file = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private string Key(int factor = 100, DateOnly? day = null, string server = Server) => ScaleTemplateKey.Compute(_root, factor, day ?? Day, server);

    [Fact]
    public void TheSameSources_GiveTheSameKey_AndItIsAHash()
    {
        Assert.Equal(Key(), Key());
        Assert.Matches("^[0-9a-f]{64}$", Key());
    }

    [Fact]
    public void OneChangedGeneratorLine_GivesAnotherKey_AndChangingItBackGivesTheFirstKeyAgain()
    {
        var before = Key();
        Write("src/Lifestream.Data/Seed/Generators.cs", "class Generators\n{\n    const int DonorsPerFactor = 41;\n}\n");
        var changed = Key();
        Write("src/Lifestream.Data/Seed/Generators.cs", "class Generators\n{\n    const int DonorsPerFactor = 40;\n}\n");

        Assert.NotEqual(before, changed);
        Assert.Equal(before, Key());
    }

    [Theory]
    [InlineData("src/Lifestream.Data/Seed/SyntheticDataBuilder.cs")] // the seed code
    [InlineData("src/Lifestream.Data/Migrations/20260101000000_Initial.cs")] // a migration
    [InlineData("src/Lifestream.Data/Lifestream.Data.csproj")]
    [InlineData("src/Lifestream.Domain/BloodType.cs")] // what the generator builds its rows from
    [InlineData("Directory.Packages.props")] // the versions of the packages that write the schema and the rows
    [InlineData("tests/Lifestream.Api.Tests/Data/SqlServerDatabaseFixture.cs")] // how the database is loaded and settled
    public void AChangeInAnyFileThatDecidesTheDatabase_GivesAnotherKey(string file)
    {
        var before = Key();
        File.AppendAllText(Path.Combine(_root, file), "// one more line\n");
        Assert.NotEqual(before, Key());
    }

    [Fact]
    public void ANewMigration_ARemovedFile_AndARenamedFile_EachGiveAnotherKey()
    {
        var before = Key();
        Write("src/Lifestream.Data/Migrations/20260201000000_Second.cs", "class Second { }\n");
        var added = Key();
        File.Delete(Path.Combine(_root, "src/Lifestream.Data/Migrations/20260201000000_Second.cs"));
        File.Move(Path.Combine(_root, "src/Lifestream.Data/Seed/SyntheticDataBuilder.cs"), Path.Combine(_root, "src/Lifestream.Data/Seed/Builder.cs"));
        var renamed = Key();
        File.Delete(Path.Combine(_root, "src/Lifestream.Data/Seed/Builder.cs"));

        Assert.NotEqual(before, added);
        Assert.NotEqual(before, renamed);
        Assert.NotEqual(before, Key());
    }

    [Fact]
    public void TheFactor_TheDay_AndTheServer_AreInTheKey()
    {
        var before = Key();
        Assert.NotEqual(before, Key(factor: 10));
        Assert.NotEqual(before, Key(day: Day.AddDays(1)));
        Assert.NotEqual(before, Key(server: "17.0.1050.2 SQL_Latin1_General_CP1_CI_AS"));
        Assert.NotEqual(before, Key(server: "17.0.1000.7 Latin1_General_100_CI_AS_SC_UTF8"));
    }

    [Fact]
    public void LineEndings_AndBuildOutput_AreNotInTheKey()
    {
        var before = Key();
        Write("src/Lifestream.Data/Seed/Generators.cs", "class Generators\r\n{\r\n    const int DonorsPerFactor = 40;\r\n}\r\n");
        Write("src/Lifestream.Data/obj/Release/Generated.cs", "class Generated { }\n");
        Write("src/Lifestream.Data/bin/Release/Notes.cs", "class Notes { }\n");
        Assert.Equal(before, Key());
    }

    [Fact]
    public void TheRealSources_InTheKey_AreTheGeneratorTheSeedCodeTheMigrationsAndTheFixture()
    {
        var root = ScaleTemplateKey.RepositoryRoot();
        Assert.NotNull(root);
        var files = ScaleTemplateKey.InputFiles(root);

        Assert.Contains("src/Lifestream.Data/Seed/Generators.cs", files);
        Assert.Contains("src/Lifestream.Data/Seed/SyntheticDataBuilder.cs", files);
        Assert.Contains("src/Lifestream.Data/LifestreamDbContext.cs", files);
        Assert.Contains("Directory.Packages.props", files);
        Assert.Contains("tests/Lifestream.Api.Tests/Data/SqlServerDatabaseFixture.cs", files);
        Assert.Contains("tests/Lifestream.Api.Tests/Data/ScaleTemplateCache.cs", files);
        Assert.Contains(files, f => f.StartsWith("src/Lifestream.Data/Migrations/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.StartsWith("src/Lifestream.Data/Entities/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.StartsWith("src/Lifestream.Domain/", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Contains("/bin/", StringComparison.Ordinal) || f.Contains("/obj/", StringComparison.Ordinal));
        Assert.Equal(files.Distinct().Order(StringComparer.Ordinal), files);
    }

    [Fact]
    public void ASourceNewerThanTheAssemblyBuiltFromIt_MeansTheKeptBackupIsNotUsed()
    {
        var output = Path.Combine(_root, "out");
        foreach (var assembly in new[] { "Lifestream.Data.dll", "Lifestream.Domain.dll", "Lifestream.Api.Tests.dll" })
        {
            Write("out/" + assembly, "");
        }

        Assert.Null(ScaleTemplateKey.StaleBuild(_root, output));

        File.SetLastWriteTimeUtc(Path.Combine(_root, "src/Lifestream.Data/Seed/Generators.cs"), DateTime.UtcNow.AddMinutes(5));
        var stale = ScaleTemplateKey.StaleBuild(_root, output);
        Assert.NotNull(stale);
        Assert.Contains("src/Lifestream.Data/Seed/Generators.cs", stale, StringComparison.Ordinal);
        Assert.Contains("Lifestream.Data.dll", stale, StringComparison.Ordinal);

        File.Delete(Path.Combine(output, "Lifestream.Domain.dll"));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "src/Lifestream.Data/Seed/Generators.cs"), DateTime.UtcNow.AddMinutes(-5));
        Assert.NotNull(ScaleTemplateKey.StaleBuild(_root, output));
    }
}
