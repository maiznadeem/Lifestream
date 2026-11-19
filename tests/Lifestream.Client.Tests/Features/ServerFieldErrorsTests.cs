using System.Net;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// FEAT-07, on the REAL API client (a stub <see cref="HttpMessageHandler"/> stands in for the server): the API's validation problem carries an <c>errors</c> object, and the
/// form shows each message under its own field instead of one generic line. Everything between the form and the handler is production code.
/// </summary>
public sealed class ServerFieldErrorsTests
{
    private const string ValidationBody =
        """{"type":"https://lifestream.example/problems/validation","title":"One or more values are not valid.","status":400,"code":"validation","correlationId":"c-1","errors":{"name":["The name is already used by another donor."],"phone":["Use digits only, with an optional leading +."]}}""";

    private static (LifestreamApiClient Api, ScriptedHandler Handler, HttpClient Http) RealClient(string body)
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.BadRequest, body));
        var http = new HttpClient(new WakeAwareHandler(new WakePolicy(), new ServerStatus(), handler)) { BaseAddress = new Uri("https://api.test/") };
        return (new LifestreamApiClient(http, new Lifestream.Client.Session.SessionService()), handler, http);
    }

    [Fact]
    public async Task DonorForm_ShowsTheServersFieldErrorsInline_ThroughTheRealClient()
    {
        var rig = new FeatureRig();
        var (api, handler, http) = RealClient(ValidationBody);
        using var _ = http;
        using var __ = api;
        var form = DonorEditorViewModel.ForNew(api, rig.Toasts, rig.Dialogs, rig.Clock);
        form.Name = "Maria Lopez";
        form.DateOfBirth = rig.Today.AddYears(-30);
        form.Gender = Gender.Female;
        form.BloodType = BloodType.APos;
        form.Frequency = DonorFrequency.Regular;
        form.Phone = "+15550123"; // valid on this side: only the server objects
        var closed = false;
        form.Closed += (_, _) => closed = true;

        await form.SaveCommand.ExecuteAsync();

        Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Post)); // it reached the server
        Assert.Equal("The name is already used by another donor.", form.GetErrorFor(nameof(DonorEditorViewModel.Name)));
        Assert.Equal("Use digits only, with an optional leading +.", form.GetErrorFor(nameof(DonorEditorViewModel.Phone)));
        Assert.False(form.HasFormError); // not one generic line instead
        Assert.False(closed); // and the form stays open with what was typed

        form.Name = "Maria Lopez-Diaz"; // editing a field clears its own message only
        Assert.Null(form.GetErrorFor(nameof(DonorEditorViewModel.Name)));
        Assert.NotNull(form.GetErrorFor(nameof(DonorEditorViewModel.Phone)));
    }

    [Fact]
    public async Task PatientForm_ShowsTheServersFieldErrorsInline_ThroughTheRealClient()
    {
        var rig = new FeatureRig();
        var (api, _, http) = RealClient(ValidationBody.Replace("another donor", "another patient", StringComparison.Ordinal));
        using var _ = http;
        using var __ = api;
        var form = PatientEditorViewModel.ForNew(api, rig.Toasts, rig.Dialogs, rig.Clock);
        form.Name = "Omar Haddad";
        form.DateOfBirth = rig.Today.AddYears(-50);
        form.Gender = Gender.Male;
        form.BloodType = BloodType.ANeg;
        form.Phone = "+15550124";

        await form.SaveCommand.ExecuteAsync();

        Assert.Equal("The name is already used by another patient.", form.GetErrorFor(nameof(PatientEditorViewModel.Name)));
        Assert.Equal("Use digits only, with an optional leading +.", form.GetErrorFor(nameof(PatientEditorViewModel.Phone)));
        Assert.False(form.HasFormError);
    }

    [Fact]
    public async Task AFieldTheFormHasNoInputFor_FallsBackToOnePlainMessage_ThroughTheRealClient()
    {
        var rig = new FeatureRig();
        var (api, _, http) = RealClient("""{"title":"One or more values are not valid.","status":400,"code":"validation","correlationId":"c-2","errors":{"medicalHold":["Not allowed."]}}""");
        using var _ = http;
        using var __ = api;
        var form = DonorEditorViewModel.ForNew(api, rig.Toasts, rig.Dialogs, rig.Clock);
        form.Name = "Maria Lopez";
        form.DateOfBirth = rig.Today.AddYears(-30);
        form.Gender = Gender.Female;
        form.BloodType = BloodType.APos;
        form.Frequency = DonorFrequency.Regular;

        await form.SaveCommand.ExecuteAsync();

        Assert.True(form.HasFormError); // there is nowhere inline to put it, so the form says so once
    }
}
