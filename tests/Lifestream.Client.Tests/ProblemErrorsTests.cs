using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Donors;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>SHELL-32 / FEAT-07: the problem-details "errors" object reaches the caller, so a form can show each message beside its field.</summary>
public sealed class ProblemErrorsTests
{
    private static async Task<ApiProblem> Fail(HttpStatusCode status, string body)
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(status, body));
        using var http = new HttpClient(new WakeAwareHandler(new WakePolicy(), new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        using var api = new LifestreamApiClient(http, new Lifestream.Client.Session.SessionService());
        var result = await api.CreateDonorAsync(new CreateDonorRequest("A", new DateOnly(1990, 1, 2), Gender.Female, BloodType.APos, null, null, DonorFrequency.Regular));
        Assert.False(result.IsOk);
        return result.Problem!;
    }

    [Fact]
    public async Task ValidationProblem_ThroughTheRealClient_CarriesTheFieldErrors()
    {
        var problem = await Fail(
            HttpStatusCode.BadRequest,
            """{"title":"Some fields need attention","status":400,"code":"validation_failed","correlationId":"c-1","errors":{"name":["Enter a name."],"phone":["Too long.","Digits only."]}}""");

        Assert.Equal(400, problem.Status);
        Assert.NotNull(problem.Errors);
        Assert.Equal(["Enter a name."], problem.Errors["name"]);
        Assert.Equal(["Too long.", "Digits only."], problem.Errors["phone"]);
        Assert.Equal(2, problem.Errors.Count);
    }

    [Theory]
    [InlineData("""{"code":"validation_failed","errors":[1,2]}""")]
    [InlineData("""{"code":"validation_failed","errors":null}""")]
    [InlineData("""{"code":"validation_failed","errors":"nope"}""")]
    [InlineData("""{"code":"validation_failed"}""")]
    public async Task ErrorsThatAreNotAnObject_AreIgnored_NotAnException(string body)
    {
        var problem = await Fail(HttpStatusCode.BadRequest, body);

        Assert.True(problem.Errors is null or { Count: 0 });
        Assert.Equal("validation_failed", problem.Code);
    }

    [Fact]
    public async Task ErrorValuesOfOddShapes_KeepTheUsableMessages()
    {
        var problem = await Fail(HttpStatusCode.BadRequest, """{"code":"validation_failed","errors":{"a":"single message","b":["ok",3,null],"c":[]}}""");

        Assert.NotNull(problem.Errors);
        Assert.Equal(["single message"], problem.Errors["a"]);
        Assert.Equal(["ok"], problem.Errors["b"]);
        Assert.DoesNotContain("c", problem.Errors.Keys);
    }
}
