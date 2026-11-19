using System.Text.Json;
using System.Text.RegularExpressions;
using Lifestream.Client.Tests;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// OWN-09: what ships on the GitHub Releases page for 2.0.0. The files that the installer, the updater and the release workflow read are checked as they are on disk: the app must point at
/// the deployed API (an https Azure host, never localhost), the update feed at the real repository, every version place at 2.0.0, and the release text must be the changelog's section
/// with nothing private in it. No window is opened and nothing is sent anywhere.
/// </summary>
public sealed partial class ReleaseConfigTests
{
    private const string Version = "2.0.0";

    private static string Text(string relative) => File.ReadAllText(RepoPaths.Find(relative)).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void ShippedAppSettings_PointAtTheDeployedHttpsAzureHost_NotLocalhost()
    {
        using var document = JsonDocument.Parse(Text("src/Lifestream.Desktop/appsettings.json"));
        var address = document.RootElement.GetProperty("Api").GetProperty("BaseAddress").GetString();

        Assert.True(Uri.TryCreate(address, UriKind.Absolute, out var uri), $"'{address}' is not an absolute address");
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.EndsWith(".azurecontainerapps.io", uri.Host, StringComparison.Ordinal);
        Assert.False(uri.IsLoopback, "the shipped app must not point at this machine");
        Assert.DoesNotContain("localhost", uri.Host, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://lifestream-api.mangotree-b12eb44b.eastus.azurecontainerapps.io/", uri.AbsoluteUri);

        // The csproj copies the file next to the exe, so the published app reads this very file.
        Assert.Contains("appsettings.json", Text("src/Lifestream.Desktop/Lifestream.Desktop.csproj"), StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateFeed_IsTheRealRepository_InTheCodeAndTheReleaseWorkflow()
    {
        Assert.Equal("https://github.com/maiznadeem/lifestream", App.ReleaseRepositoryUrl);
        Assert.Contains("REPO_URL: https://github.com/maiznadeem/lifestream", Text(".github/workflows/release.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Version_IsTwoDotZeroDotZero_EverywhereItIsSet()
    {
        Assert.Contains($"<Version>{Version}</Version>", Text("Directory.Build.props"), StringComparison.Ordinal);
        Assert.Equal(new System.Version(2, 0, 0), typeof(App).Assembly.GetName().Version is { } v ? new System.Version(v.Major, v.Minor, v.Build) : null);
        Assert.Contains($"<assemblyIdentity version=\"{Version}.0\"", Text("src/Lifestream.Desktop/app.manifest"), StringComparison.Ordinal);

        // The installer: vpk pack gets the version of the tag (v2.0.0), and the tag pattern is the only way in.
        var release = Text(".github/workflows/release.yml");
        Assert.Contains("'--packVersion', '${{ steps.version.outputs.version }}'", release, StringComparison.Ordinal);
        Assert.Contains("-p:Version=${{ steps.version.outputs.version }}", release, StringComparison.Ordinal);

        // The Store package: the manifest takes the version from the build script, which makes it three parts plus .0 (2.0.0.0).
        Assert.Contains("Version=\"{{VERSION}}\"", Text("packaging/store/AppxManifest.template.xml"), StringComparison.Ordinal);
        Assert.Contains("\"$numeric.0\"", Text("packaging/store/build-msix.ps1"), StringComparison.Ordinal);
        Assert.Contains($"git tag v{Version}", Text("packaging/store/README.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void Changelog_HasTheTwoDotZeroDotZeroSection_ForUsers_AndNothingPrivate()
    {
        var changelog = Text("CHANGELOG.md");
        var section = Section(changelog, Version);

        Assert.StartsWith("Lifestream 2.0", section, StringComparison.Ordinal);
        foreach (var heading in new[] { "### Security", "### Speed", "### Every screen", "### Demo" })
        {
            Assert.Contains(heading, section, StringComparison.Ordinal);
        }

        // Release text goes public: no credential, connection string, token, e-mail address, local path or internal run folder.
        var risky = new[]
        {
            Drive().Match(section), Url().Match(section), Secrets().Match(section), Email().Match(section),
            Internal().Match(section),
        }.Where(m => m.Success).Select(m => m.Value).ToList();
        Assert.True(risky.Count == 0, "the release text mentions: " + string.Join(", ", risky));
    }

    [Fact]
    public void ReleaseWorkflow_AttachesTheChangelogSection_AsTheReleaseText()
    {
        var release = Text(".github/workflows/release.yml");

        Assert.Contains("CHANGELOG.md", release, StringComparison.Ordinal);
        Assert.Contains("gh release edit", release, StringComparison.Ordinal);
        Assert.Contains("--notes-file", release, StringComparison.Ordinal);
        Assert.Contains("SmartScreen", release, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Install_PointsAtTheV2Release_WithSmartScreen_Checksum_AndDemo()
    {
        var readme = Text("README.md");
        var install = readme[readme.IndexOf("## Install", StringComparison.Ordinal)..readme.IndexOf("## Try it", StringComparison.Ordinal)];

        Assert.Contains("https://github.com/maiznadeem/lifestream/releases/tag/v2.0.0", install, StringComparison.Ordinal);
        Assert.Contains("More info", install, StringComparison.Ordinal);
        Assert.Contains("Run anyway", install, StringComparison.Ordinal);
        Assert.Contains("not code-signed", install, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", install, StringComparison.Ordinal);
        Assert.Contains("-Algorithm SHA256", install, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", install, StringComparison.Ordinal);
        Assert.Contains("Try the demo", install, StringComparison.Ordinal);
        Assert.DoesNotContain("Reset demo data", readme, StringComparison.Ordinal); // the app has no such button
    }

    [Fact]
    public void TheCheck_CatchesASectionThatLeaksAPathOrAConnectionString()
    {
        Assert.Matches(Drive(), @"see G:\Claude\workspace\run");
        Assert.Matches(Secrets(), "Server=tcp:db.example,1433");
        Assert.Matches(Internal(), "fixed by b-14-release-prep");
        Assert.DoesNotMatch(Drive(), "Windows 10 and 11, 64-bit");
    }

    private static string Section(string changelog, string version)
    {
        var start = changelog.IndexOf($"## [{version}]", StringComparison.Ordinal);
        Assert.True(start >= 0, $"CHANGELOG.md has no section for {version}");
        var body = changelog[(changelog.IndexOf('\n', start) + 1)..];
        var next = body.IndexOf("\n## ", StringComparison.Ordinal);
        return (next >= 0 ? body[..next] : body).Trim();
    }

    [GeneratedRegex(@"[A-Za-z]:\\|/Users/|/home/|\\\\[a-z0-9.-]+\\", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Drive();

    [GeneratedRegex(@"https?://(?!github\.com/maiznadeem/lifestream)\S+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Url();

    [GeneratedRegex(@"(?i)(password|pwd|secret|token|api[_-]?key)\s*[=:]\s*\S+|Server=|Data Source=|eyJ[A-Za-z0-9_-]{10,}|\.database\.windows\.net|\.azurecontainerapps\.io", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Secrets();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Email();

    [GeneratedRegex(@"\bb-\d{2}[a-z]?-[a-z0-9-]+|run/(agents|briefs|logs)|workspace/", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Internal();
}
