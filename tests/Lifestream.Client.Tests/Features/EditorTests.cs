using System.Text.Json;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>Donor and patient forms (FEAT-05, FEAT-07, FEAT-08), driven against the fake API client.</summary>
public sealed class EditorTests
{
    private static string Json<T>(T body)
    {
        var options = ContractJson.CreateOptions();
        options.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping; // so "+" reads as "+" in the expected text
        return JsonSerializer.Serialize(body, options);
    }

    private static void FillValidNewDonor(FeatureRig rig, DonorEditorViewModel form)
    {
        form.Name = "Maria Lopez";
        form.DateOfBirth = rig.Today.AddYears(-30);
        form.Gender = Gender.Female;
        form.BloodType = BloodType.APos;
        form.Frequency = DonorFrequency.Occasional;
    }

    // FEAT-05

    [Fact]
    public async Task DonorEdit_PrefilledAndSendsOnlyChangedFields_FakeApi()
    {
        var rig = new FeatureRig();
        var donor = Samples.Donor();
        rig.Fake.On("UpdateDonorAsync", ApiResult<DonorDetail>.Ok(donor with { Phone = "+1-555-0199", RowVersion = "rv-2" }));
        var form = rig.EditDonorForm(donor);

        // Pre-filled from the record.
        Assert.Equal(donor.Name, form.Name);
        Assert.Equal(donor.DateOfBirth, form.DateOfBirth);
        Assert.Equal(donor.Gender, form.Gender);
        Assert.Equal(donor.BloodType, form.BloodType);
        Assert.Equal(donor.Phone, form.Phone);
        Assert.Equal(donor.Address, form.Address);
        Assert.Equal(donor.Frequency, form.Frequency);
        Assert.False(form.HasUnsavedChanges);

        form.Phone = "+1-555-0199";
        await form.SaveCommand.ExecuteAsync();

        var sent = rig.Recorder.LastArg<UpdateDonorRequest>("UpdateDonorAsync");
        Assert.Equal("rv-1", sent.RowVersion);
        Assert.True(sent.Phone.HasValue);
        Assert.Equal("+1-555-0199", sent.Phone.Value);
        Assert.False(sent.Name.HasValue);
        Assert.False(sent.DateOfBirth.HasValue);
        Assert.False(sent.Gender.HasValue);
        Assert.False(sent.BloodType.HasValue);
        Assert.False(sent.Address.HasValue);
        Assert.False(sent.Frequency.HasValue);
        Assert.Equal("{\"rowVersion\":\"rv-1\",\"phone\":\"+1-555-0199\"}", Json(sent)); // what goes on the wire
    }

    [Fact]
    public async Task DonorEdit_CanClearOptionalField_SendsExplicitNull_FakeApi()
    {
        var rig = new FeatureRig();
        var donor = Samples.Donor();
        rig.Fake.On("UpdateDonorAsync", ApiResult<DonorDetail>.Ok(donor with { Address = null, RowVersion = "rv-2" }));
        var form = rig.EditDonorForm(donor);

        form.Address = string.Empty; // the user empties the optional field
        Assert.True(form.HasUnsavedChanges);
        await form.SaveCommand.ExecuteAsync();

        var sent = rig.Recorder.LastArg<UpdateDonorRequest>("UpdateDonorAsync");
        Assert.True(sent.Address.HasValue);
        Assert.Null(sent.Address.Value);
        Assert.Equal("{\"rowVersion\":\"rv-1\",\"address\":null}", Json(sent)); // present and null, not absent
    }

    [Fact]
    public async Task DonorEdit_NothingChanged_StillSendsNoFields_FakeApi()
    {
        var rig = new FeatureRig();
        var donor = Samples.Donor();
        rig.Fake.On("UpdateDonorAsync", ApiResult<DonorDetail>.Ok(donor));
        var form = rig.EditDonorForm(donor);

        form.Name = donor.Name + " "; // trailing space is not a change
        await form.SaveCommand.ExecuteAsync();

        Assert.Equal("{\"rowVersion\":\"rv-1\"}", Json(rig.Recorder.LastArg<UpdateDonorRequest>("UpdateDonorAsync")));
    }

    [Fact]
    public async Task PatientEdit_PrefilledAndSendsOnlyChangedFields_AndClearsAnOptionalField_FakeApi()
    {
        var rig = new FeatureRig();
        var patient = Samples.Patient();
        rig.Fake.On("UpdatePatientAsync", ApiResult<PatientDetail>.Ok(patient with { RowVersion = "rv-p2" }));
        var form = rig.EditPatientForm(patient);
        Assert.Equal(patient.Name, form.Name);

        form.Name = "Omar H. Haddad";
        form.Phone = string.Empty;
        await form.SaveCommand.ExecuteAsync();

        Assert.Equal("{\"rowVersion\":\"rv-p1\",\"name\":\"Omar H. Haddad\",\"phone\":null}", Json(rig.Recorder.LastArg<UpdatePatientRequest>("UpdatePatientAsync")));
    }

    [Fact]
    public async Task DonorEdit_On409_ShowsReloadPrompt_FakeApi()
    {
        var rig = new FeatureRig();
        var donor = Samples.Donor();
        rig.Fake.On("UpdateDonorAsync", ApiResult<DonorDetail>.Fail(FeatureRig.Problem(409, ProblemCodes.Concurrency)));
        var form = rig.EditDonorForm(donor);
        form.Phone = "+1-555-0150";
        var closed = false;
        form.Closed += (_, _) => closed = true;

        await form.SaveCommand.ExecuteAsync();

        Assert.True(form.HasConflict); // "changed by someone else, reload"
        Assert.Contains("changed by someone else", form.ConflictMessage, StringComparison.Ordinal);
        Assert.False(closed); // nothing was overwritten and the form stays open with the user's input
        Assert.Equal("+1-555-0150", form.Phone);
        Assert.Empty(rig.Toasts.Toasts);

        // Reload fetches the latest version, replaces the form values and clears the prompt.
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(donor with { Phone = "+1-555-0177", RowVersion = "rv-9" }));
        await form.ReloadCommand.ExecuteAsync();
        Assert.False(form.HasConflict);
        Assert.Equal("+1-555-0177", form.Phone);
        Assert.False(form.HasUnsavedChanges);

        // The next save uses the new row version.
        rig.Fake.On("UpdateDonorAsync", ApiResult<DonorDetail>.Ok(donor with { Phone = "+1-555-0188", RowVersion = "rv-10" }));
        form.Phone = "+1-555-0188";
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal("rv-9", rig.Recorder.LastArg<UpdateDonorRequest>("UpdateDonorAsync").RowVersion);
    }

    // FEAT-07

    [Fact]
    public void DonorForm_DobInFuture_InlineError_FocusMoves()
    {
        var rig = new FeatureRig();
        var form = rig.NewDonorForm();
        FillValidNewDonor(rig, form);
        form.DateOfBirth = rig.Today.AddDays(1);
        var focused = new List<string>();
        form.FocusRequested += (_, field) => focused.Add(field);

        var valid = form.ValidateAll();

        Assert.False(valid);
        Assert.Equal("The date of birth must be in the past.", form.GetError(nameof(DonorEditorViewModel.DateOfBirth)));
        Assert.Equal([nameof(DonorEditorViewModel.DateOfBirth)], focused); // focus goes to the first invalid field
        Assert.Null(form.GetError(nameof(DonorEditorViewModel.Name)));
    }

    [Fact]
    public async Task DonorForm_EmptyForm_MarksEveryRequiredField_AndSendsNothing_FakeApi()
    {
        var rig = new FeatureRig();
        var form = rig.NewDonorForm();
        var focused = new List<string>();
        form.FocusRequested += (_, field) => focused.Add(field);

        await form.SaveCommand.ExecuteAsync();

        foreach (var field in new[] { "Name", "DateOfBirth", "Gender", "BloodType", "Frequency" })
        {
            Assert.NotNull(form.GetError(field));
        }

        Assert.Null(form.GetError("Phone")); // optional
        Assert.Null(form.GetError("Address"));
        Assert.Equal(["Name"], focused); // first invalid field, in visual order
        Assert.Equal(0, rig.Recorder.CountOf("CreateDonorAsync")); // validated before submit
        Assert.Equal("5 fields need attention.", form.ErrorSummary);
    }

    [Fact]
    public void DonorForm_AgeRule_NoMinValue_NumericOnlyContact()
    {
        var rig = new FeatureRig();
        var form = rig.NewDonorForm();
        FillValidNewDonor(rig, form);
        Assert.True(form.ValidateAll());

        form.DateOfBirth = rig.Today.AddYears(-18).AddDays(1); // one day short of 18
        Assert.False(form.ValidateOnBlur("DateOfBirth"));
        Assert.Contains("at least 18", form.GetError("DateOfBirth"), StringComparison.Ordinal);
        form.DateOfBirth = rig.Today.AddYears(-18); // exactly 18 today
        Assert.True(form.ValidateOnBlur("DateOfBirth"));

        form.DateOfBirth = DateOnly.MinValue;
        Assert.False(form.ValidateOnBlur("DateOfBirth"));
        Assert.Contains("real date", form.GetError("DateOfBirth"), StringComparison.Ordinal);

        form.DateOfBirth = rig.Today.AddYears(-30);
        form.Phone = "555-ABCD";
        Assert.False(form.ValidateOnBlur("Phone"));
        Assert.Contains("digits", form.GetError("Phone"), StringComparison.Ordinal);
        form.Phone = "+1 (555) 010-1234";
        Assert.True(form.ValidateOnBlur("Phone"));
        form.Phone = string.Empty; // optional
        Assert.True(form.ValidateOnBlur("Phone"));
    }

    [Fact]
    public void PatientForm_DoesNotApplyTheDonorAgeRule_ButStillNeedsAPastDate()
    {
        var rig = new FeatureRig();
        var form = rig.NewPatientForm();
        form.Name = "Child Patient";
        form.Gender = Gender.Male;
        form.BloodType = BloodType.BPos;
        form.DateOfBirth = rig.Today.AddYears(-6);
        Assert.True(form.ValidateAll()); // a patient can be a child

        form.DateOfBirth = rig.Today;
        Assert.False(form.ValidateOnBlur("DateOfBirth"));
    }

    [Fact]
    public async Task DonorForm_ServerFieldErrors_ShownInlineUnderTheirFields_FakeApi()
    {
        var rig = new FeatureRig();
        var form = rig.NewDonorForm();
        FillValidNewDonor(rig, form);
        var detail = "{\"errors\":{\"phone\":[\"must be at most 20 characters\"],\"Name\":[\"is already taken\"]}}";
        rig.Fake.On("CreateDonorAsync", ApiResult<DonorDetail>.Fail(FeatureRig.Problem(400, ProblemCodes.Validation, "One or more values are not valid.", detail)));
        var focused = new List<string>();
        form.FocusRequested += (_, field) => focused.Add(field);

        await form.SaveCommand.ExecuteAsync();

        Assert.Equal("must be at most 20 characters", form.GetError("Phone")); // any casing of the API's field name
        Assert.Equal("is already taken", form.GetError("Name"));
        Assert.Equal(["Name"], focused); // the first one in visual order

        form.Name = "Maria Lopez-Diaz"; // editing a field clears that field's server message
        Assert.Null(form.GetError("Name"));
        Assert.NotNull(form.GetError("Phone"));
    }

    // FEAT-08

    [Fact]
    public async Task Form_Cancel_IsAlwaysEnabled_AndAsksOnlyWhenDirty_FakeApi()
    {
        var rig = new FeatureRig();
        var form = rig.NewDonorForm();
        var closed = new List<bool>();
        form.Closed += (_, e) => closed.Add(e.Saved);
        Assert.True(form.CancelCommand.CanExecute(null));

        await form.CancelCommand.ExecuteAsync(); // clean: closes without a question
        Assert.Empty(rig.Dialogs.Confirmations);
        Assert.Equal([false], closed);

        form.Name = "Typed something";
        Assert.True(form.CancelCommand.CanExecute(null)); // still enabled when dirty
        rig.Dialogs.Answer = false;
        await form.CancelCommand.ExecuteAsync();
        Assert.Single(rig.Dialogs.Confirmations);
        Assert.Equal("Discard changes?", rig.Dialogs.Confirmations[0].Title);
        Assert.Equal([false], closed); // the user chose to keep editing

        rig.Dialogs.Answer = true;
        await form.CancelCommand.ExecuteAsync();
        Assert.Equal([false, false], closed);
    }

    [Fact]
    public async Task Form_SaveButtonText_NamesTheAction_AndASecondClickIsIgnoredWhileSaving_FakeApi()
    {
        var rig = new FeatureRig();
        var form = rig.NewDonorForm();
        FillValidNewDonor(rig, form);
        var gate = new TaskCompletionSource();
        rig.Fake.On("CreateDonorAsync", async _ =>
        {
            await gate.Task;
            return ApiResult<DonorDetail>.Ok(Samples.Donor());
        });
        Assert.Equal("Add donor", form.SaveText);

        var first = form.SaveCommand.ExecuteAsync();
        Assert.True(form.SaveCommand.IsRunning); // the view shows the busy state from this
        Assert.False(form.SaveCommand.CanExecute(null));
        await form.SaveCommand.ExecuteAsync(); // a second click while busy does nothing
        gate.SetResult();
        await first;

        Assert.Equal(1, rig.Recorder.CountOf("CreateDonorAsync"));
        Assert.Contains(rig.Toasts.Toasts, t => t.Message.Contains("Donor 'Maria Lopez' added.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Form_FailedSave_KeepsInputAndShowsAPlainMessage_FakeApi()
    {
        var rig = new FeatureRig();
        var form = rig.NewDonorForm();
        FillValidNewDonor(rig, form);
        rig.Fake.On("CreateDonorAsync", ApiResult<DonorDetail>.Fail(FeatureRig.Problem(0, ClientProblemCodes.Unreachable, "The server could not be reached.")));
        var closed = false;
        form.Closed += (_, _) => closed = true;

        await form.SaveCommand.ExecuteAsync();

        Assert.False(closed);
        Assert.Equal("Maria Lopez", form.Name);
        Assert.Contains("Can't reach the server", form.FormError, StringComparison.Ordinal);
        Assert.True(form.HasUnsavedChanges);
    }

    [Fact]
    public void Form_LabelsAndNames_SupportLongAndNonLatinNames()
    {
        var rig = new FeatureRig();
        var form = rig.NewPatientForm();
        form.Name = "Ayşe Nur Öztürk-Abdulrahman";
        form.Gender = Gender.Female;
        form.BloodType = BloodType.ONeg;
        form.DateOfBirth = rig.Today.AddYears(-40);
        Assert.True(form.ValidateAll());

        form.Name = new string('x', 101);
        Assert.False(form.ValidateOnBlur("Name"));
        form.Name = new string('ö', 100);
        Assert.True(form.ValidateOnBlur("Name"));
    }
}
