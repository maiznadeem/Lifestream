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
