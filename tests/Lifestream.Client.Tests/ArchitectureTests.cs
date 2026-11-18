namespace Lifestream.Client.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("System.Data.SqlClient")]
    [InlineData("PresentationFramework")]
    public void Client_DoesNotReference_Database_Or_Wpf(string forbiddenPrefix)
    {
        var references = typeof(ClientAssembly).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, r => r.Name!.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
    }
}
