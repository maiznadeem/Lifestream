using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lifestream.Data;

/// <summary>Used only by <c>dotnet ef</c>. Contains no credentials: Windows authentication to a local server, never connected for scripts.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<LifestreamDbContext>
{
    public LifestreamDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("LIFESTREAM_TEST_SQL")
            ?? "Server=localhost;Integrated Security=true;TrustServerCertificate=true";
        var builder = new DbContextOptionsBuilder<LifestreamDbContext>().UseSqlServer(connection + ";Database=Lifestream_DesignTime");
        return new LifestreamDbContext(builder.Options);
    }
}
