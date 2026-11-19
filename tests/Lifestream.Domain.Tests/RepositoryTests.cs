using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lifestream.Domain.Tests;

/// <summary>Checks on the repository itself: layout, build rules, CI, hygiene and documents (OWN-08, INFRA-14 to INFRA-20).</summary>
public sealed partial class RepositoryTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lifestream.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Lifestream.slnx not found above the test output folder.");
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    private static string[] GitTrackedFiles()
    {
        var startInfo = new ProcessStartInfo("git", "ls-files")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<string> ProjectAndPropsFiles() =>
        Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // ---- OWN-08: layout ----

    [Theory]
    [InlineData("Domain")]
    [InlineData("Contracts")]
    [InlineData("Data")]
    [InlineData("Api")]
    [InlineData("Client")]
    [InlineData("Desktop")]
    public void Layout_EachSourceProject_ExistsAndIsInTheSolution(string name)
    {
        Assert.True(File.Exists(Path.Combine(Root, "src", $"Lifestream.{name}", $"Lifestream.{name}.csproj")));
        Assert.Contains($"src/Lifestream.{name}/Lifestream.{name}.csproj", Read("Lifestream.slnx"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Api")]
    [InlineData("Client")]
    [InlineData("Desktop")]
    public void Layout_EachTestProject_ExistsAndIsInTheSolution(string name)
    {
        Assert.True(File.Exists(Path.Combine(Root, "tests", $"Lifestream.{name}.Tests", $"Lifestream.{name}.Tests.csproj")));
        Assert.Contains($"tests/Lifestream.{name}.Tests/Lifestream.{name}.Tests.csproj", Read("Lifestream.slnx"), StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_OldFrameworkProject_IsGone()
    {
        Assert.False(File.Exists(Path.Combine(Root, "WPFApp.csproj")));
        Assert.False(File.Exists(Path.Combine(Root, "packages.config")));
        Assert.False(Directory.Exists(Path.Combine(Root, "Views")));
        Assert.False(Directory.Exists(Path.Combine(Root, "Repositories")));
    }

    [Fact]
    public void Sdk_IsPinnedToEleven_AndProjectsTargetNet11()
    {
        Assert.Matches(@"""version""\s*:\s*""11\.0\.\d+""", Read("global.json"));
        Assert.Contains("<TargetFramework>net11.0</TargetFramework>", Read("Directory.Build.props"), StringComparison.Ordinal);
        Assert.Contains("net11.0-windows", Read("src/Lifestream.Desktop/Lifestream.Desktop.csproj"), StringComparison.Ordinal);
    }

    // ---- INFRA-15: build rules ----

    [Fact]
    public void Build_WarningsAreErrors_NullableOn_AnalyzersConfigured()
    {
        var props = XDocument.Parse(Read("Directory.Build.props"));
        string? Value(string name) => props.Descendants(name).Select(e => e.Value).SingleOrDefault();

        Assert.Equal("true", Value("TreatWarningsAsErrors"));
        Assert.Equal("enable", Value("Nullable"));
        Assert.Equal("true", Value("EnforceCodeStyleInBuild"));
        Assert.Equal("latest-recommended", Value("AnalysisLevel"));
        Assert.Equal("true", Value("GenerateDocumentationFile")); // required for IDE0005 to run in the build

        var editorConfig = Read(".editorconfig");
        Assert.Contains("dotnet_diagnostic.IDE0005.severity = error", editorConfig, StringComparison.Ordinal); // unused usings
        Assert.Contains("dotnet_diagnostic.IDE0051.severity = error", editorConfig, StringComparison.Ordinal); // unused private members
        Assert.Contains("dotnet_diagnostic.CA2000.severity = error", editorConfig, StringComparison.Ordinal); // undisposed resources
    }

    [Fact]
    public void Build_FailsOnPlantedUnusedUsingAndUndisposedResource_AndPassesWhenClean()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? throw new InvalidOperationException("DOTNET_HOST_PATH is not set; run through 'dotnet test'.");
        var work = Path.Combine(Path.GetTempPath(), "lifestream-planted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (var file in new[] { "Directory.Build.props", ".editorconfig", "global.json" })
            {
                File.Copy(Path.Combine(Root, file), Path.Combine(work, file));
            }

            File.WriteAllText(Path.Combine(work, "Planted.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");

            File.WriteAllText(Path.Combine(work, "Program.cs"), "using System.Text;\nvar stream = File.Open(\"x.tmp\", FileMode.OpenOrCreate);\nConsole.WriteLine(stream.Length);\n");
            var (failedExit, failedOutput) = Build(dotnet, work);
            Assert.NotEqual(0, failedExit);
            Assert.Contains("IDE0005", failedOutput, StringComparison.Ordinal);
            Assert.Contains("CA2000", failedOutput, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(work, "Program.cs"), "using var stream = File.Open(\"x.tmp\", FileMode.OpenOrCreate);\nConsole.WriteLine(stream.Length);\n");
            var (cleanExit, cleanOutput) = Build(dotnet, work);
            Assert.True(cleanExit == 0, cleanOutput);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static (int ExitCode, string Output) Build(string dotnet, string directory)
    {
        var startInfo = new ProcessStartInfo(dotnet, "build -c Release --nologo")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }

    // ---- INFRA-16: packages ----

    [Fact]
    public void Packages_AreCentral_Audited_AndNoBannedPackagesReferenced()
    {
        var central = XDocument.Parse(Read("Directory.Packages.props"));
        Assert.Equal("true", central.Descendants("ManagePackageVersionsCentrally").Single().Value);

        var props = XDocument.Parse(Read("Directory.Build.props"));
        Assert.Equal("true", props.Descendants("NuGetAudit").Single().Value);
        Assert.Equal("high", props.Descendants("NuGetAuditLevel").Single().Value);
        Assert.Contains("NU1903", props.Descendants("WarningsAsErrors").Single().Value, StringComparison.Ordinal);
        Assert.Equal("true", props.Descendants("TreatWarningsAsErrors").Single().Value);

        var banned = new[] { "System.Drawing.Common", "LiveCharts", "LiveCharts.Wpf", "LiveCharts.Core" };
        var offenders = new List<string>();
        foreach (var file in ProjectAndPropsFiles())
        {
            var document = XDocument.Load(file);
            foreach (var reference in document.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion"))
            {
                var id = (string?)reference.Attribute("Include") ?? string.Empty;
                if (banned.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {id}");
                }

                if (reference.Name.LocalName == "PackageReference" && reference.Attribute("Version") is not null)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {id} sets Version outside Directory.Packages.props");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Packages_EveryReferencedPackage_HasACentralVersion()
    {
        var central = XDocument.Parse(Read("Directory.Packages.props")).Descendants("PackageVersion")
            .Select(e => (string)e.Attribute("Include")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = ProjectAndPropsFiles()
            .SelectMany(f => XDocument.Load(f).Descendants("PackageReference"))
            .Select(e => (string)e.Attribute("Include")!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        Assert.All(referenced, id => Assert.Contains(id, central));
    }

    [Fact]
    public void Dependabot_IsEnabled_ForNugetAndGithubActions()
    {
        var dependabot = Read(".github/dependabot.yml");

        Assert.Matches(@"package-ecosystem:\s*nuget", dependabot);
        Assert.Matches(@"package-ecosystem:\s*github-actions", dependabot);
        Assert.Matches(@"interval:\s*weekly", dependabot);
    }

    // ---- INFRA-14: CI ----

    [Fact]
    public void Ci_RunsBuildAndAllTests_OnPullRequests_AndNeverContinuesOnError()
    {
        var ci = Read(".github/workflows/ci.yml");

        Assert.Matches(@"(?m)^\s*pull_request:", ci);
        Assert.Contains("dotnet build", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test tests/Lifestream.Domain.Tests", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test tests/Lifestream.Client.Tests", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test tests/Lifestream.Api.Tests", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test tests/Lifestream.Desktop.Tests", ci, StringComparison.Ordinal);
        Assert.Contains("mssql/server:2025-latest", ci, StringComparison.Ordinal);
        Assert.Contains("LIFESTREAM_TEST_SQL", ci, StringComparison.Ordinal);
        Assert.Contains("windows-latest", ci, StringComparison.Ordinal);
        Assert.Contains("ubuntu-latest", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error: true", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_RunsEveryTestProjectUnderTwoFixedSeeds_SoAnOrderDependenceFailsThePullRequest()
    {
        // xUnit's default order is derived from the assembly path, so a test that leaks state passes in one checkout and fails in another.
        // A seeded order is the same on every machine: CI runs each project under two fixed seeds and fails the job when either run fails.
        var ci = Read(".github/workflows/ci.yml");
        var job = Regex.Match(ci, @"(?ms)^  test-order:\r?\n(?<body>.*?)(?=^  \S|\z)");
        Assert.True(job.Success, "ci.yml has no test-order job");
        var body = job.Groups["body"].Value;

        foreach (var project in new[] { "Domain", "Client", "Api", "Desktop" })
        {
            Assert.Matches($@"(?m)^\s*projects:\s*""[^""\r\n]*\b{project}\b", body);
        }

        var seeds = Regex.Match(body, @"(?m)^\s*SEEDS:\s*""(?<seeds>\d+( \d+)+)""").Groups["seeds"].Value.Split(' ');
        Assert.True(seeds.Distinct().Count() >= 2, "the test-order job needs at least two distinct fixed seeds");
        Assert.Contains("\":$seed\"", body, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\s*pull_request:", ci);
        Assert.DoesNotContain("continue-on-error", body, StringComparison.Ordinal);
        Assert.Contains("exit $fail", body, StringComparison.Ordinal);
        Assert.Contains("-notrait \"Category=Perf\"", body, StringComparison.Ordinal); // running the exe directly skips the csproj filter: wall-clock tests stay out of it too
    }

    // ---- INFRA-18: repository hygiene ----

    [Theory]
    [InlineData("*.bak")]
    [InlineData("*.bacpac")]
    [InlineData("*.rar")]
    [InlineData("*.pfx")]
    [InlineData("publish/")]
    [InlineData(".env")]
    public void GitIgnore_ContainsPattern(string pattern)
    {
        var lines = Read(".gitignore").Split('\n', StringSplitOptions.TrimEntries);

        Assert.Contains(pattern, lines);
    }

    [Fact]
    public void Git_TracksNoDumpBackupArchiveCertificateOrAutorun()
    {
        var forbidden = ForbiddenTrackedFile();

        var offenders = GitTrackedFiles().Where(f => forbidden.IsMatch(Path.GetFileName(f)) || f.StartsWith("publish/", StringComparison.Ordinal)).ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Git_TracksNoExtensionlessDatabaseFileAtRoot_AndImagesLiveUnderDocs()
    {
        var tracked = GitTrackedFiles();

        Assert.DoesNotContain("BloodBank", tracked);
        var strayMedia = tracked
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.StartsWith("docs/", StringComparison.Ordinal) && !f.StartsWith("tests/captures/", StringComparison.Ordinal)
                && !f.StartsWith("packaging/", StringComparison.Ordinal) && !f.Contains("/Assets/", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(strayMedia);
    }

    [GeneratedRegex(@"\.(bak|bacpac|rar|pfx|dacpac|mdf|ldf)$|^autorun\.inf$|^\.env$", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenTrackedFile();

    [Fact]
    public void Readme_VersionMatchesBuildVersion_AndPointsAtDocs()
    {
        var version = XDocument.Parse(Read("Directory.Build.props")).Descendants("Version").Single().Value;
        var readme = Read("README.md");

        Assert.Contains($"**{version}**", readme, StringComparison.Ordinal);
        Assert.Contains($"# Lifestream {version[..version.LastIndexOf('.')]}", readme, StringComparison.Ordinal);
        Assert.Contains("docs/data-retention.md", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("1.0.0", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("setup.exe", readme, StringComparison.Ordinal);
    }

    // ---- INFRA-20: retention rule and synthetic data ----

    [Fact]
    public void DataRetention_Document_StatesRetentionAndDeletionRuleAndSyntheticDemo()
    {
        var text = Read("docs/data-retention.md");

        Assert.Contains("donor", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("patient", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Deletion on request", text, StringComparison.Ordinal);
        Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reviewed once a year", text, StringComparison.Ordinal);
        Assert.Matches(@"\d+ months", text);
    }

    [Theory]
    [InlineData("INFRA-14")]
    [InlineData("INFRA-15")]
    [InlineData("INFRA-16")]
    [InlineData("INFRA-18")]
    [InlineData("INFRA-20")]
    [InlineData("API-45")]
    [InlineData("DB-08")]
    public void Requirements_Document_ListsItem(string id)
    {
        Assert.Contains($"| {id} |", Read("docs/requirements.md"), StringComparison.Ordinal);
    }
}
