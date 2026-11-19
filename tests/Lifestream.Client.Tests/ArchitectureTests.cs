using System.Text.RegularExpressions;
using Lifestream.Client.Common;

namespace Lifestream.Client.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("System.Data.SqlClient")]
    [InlineData("Lifestream.Data")]
    [InlineData("PresentationFramework")]
    public void Client_DoesNotReference_Database_Or_Wpf(string forbiddenPrefix)
    {
        var references = typeof(ClientAssembly).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, r => r.Name!.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void ClientSource_NeverBlocksOnATask()
    {
        var root = Path.GetDirectoryName(RepoPaths.Find("src/Lifestream.Client/Lifestream.Client.csproj"))!;
        var blocking = new Regex(@"\.GetAwaiter\(\)\s*\.GetResult\(\)|\.Wait\(|\.WaitAll\(|\.WaitAny\(|\.Result(?!\w)", RegexOptions.None, TimeSpan.FromSeconds(5));

        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line.Trim())))
            .Where(l => !l.Text.StartsWith("//", StringComparison.Ordinal) && !l.Text.StartsWith("///", StringComparison.Ordinal) && blocking.IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text}")
            .ToList();

        Assert.Empty(offenders); // sync-over-async on the UI thread deadlocks (SHELL-09, SHELL-17)
    }

    [Fact]
    public void ClientProject_HasNoPackageOrProjectReference_ToDataAccess()
    {
        var project = string.Concat(
            System.Xml.Linq.XDocument.Load(RepoPaths.Find("src/Lifestream.Client/Lifestream.Client.csproj"))
                .Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference" or "Reference")
                .Select(e => (string?)e.Attribute("Include") + ";"));

        Assert.DoesNotContain("EntityFramework", project, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlClient", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Lifestream.Data", project, StringComparison.Ordinal);
    }

    [Fact]
    public void NoViewModel_InheritsFromARepositoryBase()
    {
        var offenders = typeof(ClientAssembly).Assembly.GetTypes()
            .Where(t => typeof(ViewModelBase).IsAssignableFrom(t))
            .Where(t =>
            {
                for (var b = t.BaseType; b is not null; b = b.BaseType)
                {
                    if (b.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            })
            .Select(t => t.Name);

        Assert.Empty(offenders);
        Assert.DoesNotContain(typeof(ClientAssembly).Assembly.GetTypes(), t => t.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClientSource_ContainsNoSqlOrRepositoryCalls()
    {
        var forbidden = new Regex(@"SqlConnection|SqlCommand|DbContext|IRepository|Repository\b|SELECT\s+.+\s+FROM", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Find("src/Lifestream.Client"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(forbidden, File.ReadAllText(file));
        }
    }
}
