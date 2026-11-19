namespace Lifestream.Api.Tests.Data;

/// <summary>
/// The test fixture creates and drops databases by name, so a name is checked before it reaches the server (only Lifestream_Test_ and 32 lower-case hex
/// digits are accepted) and travels as a parameter that the server quotes. A name with a bracket or a semicolon is never executed.
/// </summary>
public sealed class FixtureDatabaseNameTests(SqlServerDatabaseFixture fx) : IClassFixture<SqlServerDatabaseFixture>
{
    private const string Hex = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("Lifestream_Test_" + Hex, true)]
    [InlineData("Lifestream_Test_" + Hex + "]; DROP DATABASE [master]; --", false)]
    [InlineData("Lifestream_Test_" + Hex + ";", false)]
    [InlineData("Lifestream_Test_" + Hex + "\n", false)]
    [InlineData("Lifestream_Test_" + Hex + "0", false)]
    [InlineData("Lifestream_Test_0123456789abcdef0123456789abcde", false)]
    [InlineData("Lifestream_Test_0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("master", false)]
    [InlineData("Lifestream_Perf_" + Hex, false)]
    [InlineData("", false)]
    public void OnlyTheNamesTheFixtureGeneratesAreAccepted(string name, bool accepted) =>
        Assert.Equal(accepted, SqlServerDatabaseFixture.IsFixtureDatabaseName(name));

    [Fact]
    public async Task ADatabaseNameWithABracketOrASemicolon_IsRefusedBeforeItReachesTheServer_AndTheRealDatabaseSurvives()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.StartsWith("Microsoft SQL Server", fx.ServerVersion, StringComparison.Ordinal); // a real SQL Server, not an in-memory fallback

        foreach (var evil in new[] { $"{fx.DatabaseName}]; DROP DATABASE [{fx.DatabaseName}]; --", $"{fx.DatabaseName};", "x' + (SELECT 1) + '" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fx.DropDatabaseNamedAsync(evil, ct));
        }

        Assert.Equal(1, await fx.ScalarAsync<int>("SELECT 1", ct)); // the fixture's own database is still there and answers
    }
}
