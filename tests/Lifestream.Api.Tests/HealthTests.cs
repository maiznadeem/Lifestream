using System.Net;
using Lifestream.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

public sealed class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_Healthz_Returns200_WithoutDatabase()
    {
        // No connection string is configured anywhere: the host must still start and answer.
        using var client = _factory.CreateClient();
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();
        Assert.Null(configuration.GetConnectionString("Lifestream"));

        using var response = await client.GetAsync(ApiRoutes.Healthz, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
