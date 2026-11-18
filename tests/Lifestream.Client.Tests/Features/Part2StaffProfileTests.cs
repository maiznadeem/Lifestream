using System.Reflection;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Navigation;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Staff;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>Staff (list, editor, reset password, delete rules; FEAT-20 to FEAT-23) and Profile (FEAT-24 to FEAT-26), against the fake API client.</summary>
public sealed class Part2StaffProfileTests
{
    private static async Task<(Part2Rig Rig, StaffViewModel Vm)> LoadedStaffAsync(Part2Rig? rig = null, params StaffListItem[] rows)
    {
        rig ??= new Part2Rig();
        rows = rows.Length == 0 ? [Samples2.StaffRow(1, "Test Admin", true, 5000, Role.Admin), Samples2.StaffRow(2, "Staff 002", true), Samples2.StaffRow(3, "Staff 003", false)] : rows;
        rig.Fake.On("GetStaffAsync", ApiResult<PagedResult<StaffListItem>>.Ok(new(rows, rows.Length, 1, 50)));
        rig.Fake.On("GetStaffPhotoAsync", ApiResult<byte[]>.Ok([1, 2, 3]));
        rig.Fake.On("GetStaffMemberAsync", _ => Task.FromResult(ApiResult<StaffDetail>.Ok(Samples2.Staff(rig.Recorder.LastArg<int>("GetStaffMemberAsync"), "Selected"))));
        var vm = rig.Staff();
        await vm.LoadAsync();
        return (rig, vm);
    }

    private static async Task SelectAsync(StaffViewModel vm, int index)
    {
        vm.SelectedItem = vm.Items[index];
        await vm.PendingDetail;
    }

    // FEAT-20

    [Fact]
    public async Task StaffList_NoPasswordColumn_LazyThumbnails()
    {
        var (rig, vm) = await LoadedStaffAsync();

        // No password and no photo bytes come with the list; the thumbnails are fetched later, one per row that has a photo.
        Assert.Equal(1, rig.Recorder.CountOf("GetStaffAsync"));
        Assert.Equal(0, rig.Recorder.CountOf("GetStaffPhotoAsync"));
        Assert.DoesNotContain(typeof(StaffRow).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(StaffListItem).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || p.PropertyType == typeof(byte[]));
        Assert.DoesNotContain(typeof(StaffDetail).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || p.PropertyType == typeof(byte[]));
        Assert.All(vm.Items, row => Assert.Null(row.Photo)); // the default avatar shows until the bytes arrive

        var withPhoto = vm.Items[1];
        await vm.LoadPhotoCommand.ExecuteAsync(withPhoto);
        Assert.Equal([1, 2, 3], withPhoto.Photo);
        await vm.LoadPhotoAsync(withPhoto); // shown again (scrolled back): no second request
        await vm.LoadPhotoAsync(vm.Items[2]); // no photo: no request at all
        Assert.Equal(1, rig.Recorder.CountOf("GetStaffPhotoAsync"));
        Assert.Null(vm.Items[2].Photo);
    }

    [Fact]
    public async Task StaffList_PhotoRowChangesNotify_SoTheCellUpdatesWhenTheBytesArrive()
    {
        var (_, vm) = await LoadedStaffAsync();
        var raised = new List<string?>();
        vm.Items[1].PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.LoadPhotoAsync(vm.Items[1]);

        Assert.Contains(nameof(StaffRow.Photo), raised);
    }

    [Fact]
    public async Task StaffList_ThumbnailFailure_LeavesTheDefaultAvatar_NoCrash()
    {
        var (rig, vm) = await LoadedStaffAsync();
        rig.Fake.On("GetStaffPhotoAsync", FakeApiClient.Unreachable<byte[]>());

        await vm.LoadPhotoAsync(vm.Items[1]);

        Assert.Null(vm.Items[1].Photo);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task StaffSalaryRange_AcceptsDigitsOnly_AndSaysWhenFromExceedsTo()
    {
        var (rig, vm) = await LoadedStaffAsync(null, Samples2.StaffRow(1, "A", false, 2000), Samples2.StaffRow(2, "B", false, 4000), Samples2.StaffRow(3, "C", false, 6000), Samples2.StaffRow(4, "D", false, null));

        vm.SalaryFromText = "3,0a0 ";
        Assert.Equal("300", vm.SalaryFromText); // letters, commas and spaces are dropped as they arrive
        await vm.PendingRefresh;
        vm.SalaryFromText = "3000";
        await vm.PendingRefresh;
        Assert.Equal(["B", "C"], vm.Items.Select(i => i.DisplayName)); // From 3000: 4000 and 6000

        vm.SalaryToText = "5000";
        await vm.PendingRefresh;
        Assert.Equal(["B"], vm.Items.Select(i => i.DisplayName));

        var calls = rig.Recorder.CountOf("GetStaffAsync");
        vm.SalaryToText = "1000"; // From 3000 is higher than To 1000
        Assert.Equal("From can't be higher than To.", vm.SalaryRangeError);
        Assert.True(vm.HasSalaryRangeError);
        Assert.Equal(calls, rig.Recorder.CountOf("GetStaffAsync")); // nothing is fetched while the range is wrong

        vm.SalaryToText = "9000";
        await vm.PendingRefresh;
        Assert.Null(vm.SalaryRangeError);
        Assert.Equal(["B", "C"], vm.Items.Select(i => i.DisplayName));
        Assert.Equal(2, rig.Recorder.CountOf("GetStaffAsync")); // the initial list and one full fetch; typing the rest of the range reused it
        Assert.Equal(200, rig.Recorder.Calls.Last(c => c.Name == "GetStaffAsync").Args[2]); // the full fetch is one call of the maximum page size

        await vm.ClearFiltersCommand.ExecuteAsync();
        Assert.Equal(string.Empty, vm.SalaryFromText);
        Assert.Equal(4, vm.Items.Count);
    }

    [Fact]
    public async Task StaffList_RowsAreFilteredByTheSearchText_ThroughTheApi()
    {
        var (rig, vm) = await LoadedStaffAsync();
        vm.SearchText = "doctor";
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Equal("doctor", rig.Recorder.Calls.Last(c => c.Name == "GetStaffAsync").Args[0]);
    }

    // FEAT-21

    [Fact]
    public void StaffEditor_NewStartsEmptyDobAndSalary_PasswordMaskedWithConfirm()
    {
        var rig = new Part2Rig();
        var form = StaffEditorViewModel.ForNew(rig.Api, rig.Toasts, rig.Dialogs, rig.Clock);

        Assert.Null(form.DateOfBirth); // no 1/1/0001 and no made-up date
        Assert.Equal(string.Empty, form.SalaryText);
        Assert.Null(form.Gender);
        Assert.Null(form.Designation);
        Assert.Null(form.Shift);
        Assert.Equal(Role.Staff, form.Role); // the least-privileged role, chosen deliberately
        Assert.True(form.ShowPasswordFields);
        Assert.Equal(string.Empty, form.Password);
        Assert.Equal(string.Empty, form.ConfirmPassword);
        Assert.False(form.HasUnsavedChanges);

        Assert.False(form.ValidateAll());
        Assert.Equal("Enter the date of birth.", form.GetError(nameof(StaffEditorViewModel.DateOfBirth)));
        Assert.Equal("Enter a password.", form.GetError(nameof(StaffEditorViewModel.Password)));
        Assert.Equal("Type the password again.", form.GetError(nameof(StaffEditorViewModel.ConfirmPassword)));
        Assert.Null(form.GetError(nameof(StaffEditorViewModel.SalaryText))); // optional

        form.Password = "short";
        form.ConfirmPassword = "different";
        Assert.False(form.ValidateAll());
        Assert.Contains("at least 12", form.GetError(nameof(StaffEditorViewModel.Password))!, StringComparison.Ordinal);
        Assert.Equal("The two passwords don't match.", form.GetError(nameof(StaffEditorViewModel.ConfirmPassword)));

        form.SalaryText = "12a";
        form.ValidateAll();
        Assert.Equal("Use digits only.", form.GetError(nameof(StaffEditorViewModel.SalaryText)));
    }

    [Fact]
    public async Task StaffEditor_New_SendsTheForm_ClearsThePasswords_AndTurnsAUsernameClashIntoAMessage()
    {
        var rig = new Part2Rig();
        rig.Fake.On("CreateStaffAsync", FakeApiClient.Fail<StaffDetail>(409, ProblemCodes.Conflict, "exists"));
        var form = StaffEditorViewModel.ForNew(rig.Api, rig.Toasts, rig.Dialogs, rig.Clock);
        form.DisplayName = "Nurse Joy";
        form.Username = "nurse.joy";
        form.Gender = Gender.Female;
        form.DateOfBirth = new DateOnly(1990, 1, 1);
        form.Designation = Designation.Assistant;
        form.Shift = Shift.FullTime;
        form.SalaryText = "4200";
        form.Password = "a-long-enough-password";
        form.ConfirmPassword = "a-long-enough-password";

        await form.SaveCommand.ExecuteAsync();

        Assert.Equal("That username is already taken. Choose another.", form.GetError(nameof(StaffEditorViewModel.Username)));
        var sent = rig.Recorder.LastArg<CreateStaffRequest>("CreateStaffAsync");
        Assert.Equal(("nurse.joy", 4200, Role.Staff, "a-long-enough-password"), (sent.Username, sent.Salary, sent.Role, sent.InitialPassword));

        rig.Fake.On("CreateStaffAsync", ApiResult<StaffDetail>.Ok(Samples2.Staff(9, "Nurse Joy")));
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(string.Empty, form.Password); // never kept once saved
        Assert.Equal(string.Empty, form.ConfirmPassword);
    }

    [Fact]
    public async Task StaffEditor_Edit_NeverShowsOrSendsAPassword_AndSendsOnlyChangedFields()
    {
        var rig = new Part2Rig();
        rig.Fake.On("UpdateStaffAsync", ApiResult<StaffDetail>.Ok(Samples2.Staff()));
        var form = StaffEditorViewModel.ForEdit(rig.Api, rig.Toasts, rig.Dialogs, Samples2.Staff(), rig.Clock);

        Assert.False(form.ShowPasswordFields);
        Assert.False(form.CanEditUsername);
        Assert.False(form.HasUnsavedChanges);

        form.SalaryText = string.Empty; // cleared: an explicit null
        form.Shift = Shift.Evening;
        await form.SaveCommand.ExecuteAsync();

        var sent = rig.Recorder.LastArg<UpdateStaffRequest>("UpdateStaffAsync");
        Assert.True(sent.Shift.HasValue);
        Assert.True(sent.Salary.HasValue);
        Assert.Null(sent.Salary.Value);
        Assert.False(sent.DisplayName.HasValue);
        Assert.False(sent.Phone.HasValue);
        Assert.Equal("rv-s1", sent.RowVersion);
        Assert.DoesNotContain(typeof(UpdateStaffRequest).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    // FEAT-23

    [Fact]
    public async Task StaffDelete_OwnRecordDisabledWithTooltip()
    {
        var (rig, vm) = await LoadedStaffAsync();

        await SelectAsync(vm, 0); // the signed-in admin's own record (id 1)
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.Equal("You can't delete your own account.", vm.DeleteDisabledReason);
        Assert.True(vm.HasDeleteDisabledReason);

        await SelectAsync(vm, 1);
        Assert.True(vm.DeleteCommand.CanExecute(null));
        Assert.Null(vm.DeleteDisabledReason);
        Assert.False(vm.HasDeleteDisabledReason);
        Assert.Equal(0, rig.Recorder.CountOf("DeleteStaffAsync"));
    }

    [Fact]
    public async Task StaffDelete_EveryOtherDelete_AsksFirst_NamingThePerson_NoUndoOffered()
    {
        var (rig, vm) = await LoadedStaffAsync();
        rig.Fake.OnVoid("DeleteStaffAsync", ApiResult.Success);
        await SelectAsync(vm, 1);

        rig.Dialogs.Answer = false;
        await vm.DeleteCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("DeleteStaffAsync")); // declined: nothing deleted

        rig.Dialogs.Answer = true;
        await vm.DeleteCommand.ExecuteAsync();

        Assert.Equal(2, rig.Dialogs.Confirmations.Count);
        Assert.Contains("Selected", rig.Dialogs.Confirmations[1].Body, StringComparison.Ordinal);
        Assert.Contains("can't be undone", rig.Dialogs.Confirmations[1].Body, StringComparison.Ordinal);
        Assert.Equal(1, rig.Recorder.CountOf("DeleteStaffAsync"));
        var toast = Assert.Single(rig.Toasts.Toasts);
        Assert.False(toast.HasAction); // a deleted staff member can't be restored from the list, so no Undo
    }

    [Fact]
    public async Task StaffDelete_ProtectedAccount_IsDisabledWithTheReason()
    {
        var rig = new Part2Rig();
        var (_, vm) = await LoadedStaffAsync(rig);
        rig.Fake.On("GetStaffMemberAsync", ApiResult<StaffDetail>.Ok(Samples2.Staff(2, "Owner", isProtected: true, role: Role.Admin)));

        await SelectAsync(vm, 1);

        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.Equal("This account is protected and can't be deleted.", vm.DeleteDisabledReason);
    }

    [Fact]
    public async Task StaffScreen_DemoAccount_CanViewButNotChange()
    {
        var rig = new Part2Rig(Role.Staff, demo: true);
        var (_, vm) = await LoadedStaffAsync(rig);
        await SelectAsync(vm, 1);

        Assert.False(vm.CanManageStaff);
        Assert.Null(vm.NewCommand); // no Ctrl+N
        Assert.NotNull(vm.ReadOnlyNotice);
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.ResetPasswordCommand.CanExecute(null));
        Assert.Equal("The demo account can't delete staff.", vm.DeleteDisabledReason);
    }

    // FEAT-22

    [Fact]
    public async Task ResetPassword_DialogHasMaskedFieldsWithConfirm_NeverShowsAPassword_ThenToasts()
    {
        var (rig, vm) = await LoadedStaffAsync();
        rig.Fake.OnVoid("ResetStaffPasswordAsync", ApiResult.Success);
        await SelectAsync(vm, 1);
        Assert.True(vm.ResetPasswordCommand.CanExecute(null));
        ResetPasswordDialogViewModel? dialog = null;
        rig.Forms.Script = async shown =>
        {
            dialog = Assert.IsType<ResetPasswordDialogViewModel>(shown);
            Assert.Equal(string.Empty, dialog.TemporaryPassword); // starts empty: nothing stored is ever shown
            Assert.Equal(string.Empty, dialog.ConfirmPassword);

            dialog.TemporaryPassword = "short";
            dialog.ConfirmPassword = "other";
            await dialog.PrimaryCommand.ExecuteAsync();
            Assert.False(dialog.Completed);
            Assert.Equal(0, rig.Recorder.CountOf("ResetStaffPasswordAsync")); // invalid: nothing sent
            Assert.Contains("at least 12", dialog.GetError(nameof(ResetPasswordDialogViewModel.TemporaryPassword))!, StringComparison.Ordinal);
            Assert.Equal("The two passwords don't match.", dialog.GetError(nameof(ResetPasswordDialogViewModel.ConfirmPassword)));

            dialog.TemporaryPassword = "Temporary-Pass-12";
            dialog.ConfirmPassword = "Temporary-Pass-12";
            await dialog.PrimaryCommand.ExecuteAsync();
        };

        await vm.ResetPasswordCommand.ExecuteAsync();

        var request = (ResetPasswordRequest)rig.Recorder.Calls.Single(c => c.Name == "ResetStaffPasswordAsync").Args[1]!;
        Assert.Equal("Temporary-Pass-12", request.TemporaryPassword);
        Assert.Equal(2, rig.Recorder.Calls.Single(c => c.Name == "ResetStaffPasswordAsync").Args[0]);
        var toast = Assert.Single(rig.Toasts.Toasts);
        Assert.Equal("Password reset for Selected. They must choose a new one at next sign-in.", toast.Message);
        Assert.DoesNotContain("Temporary-Pass-12", toast.Message, StringComparison.Ordinal); // no password is ever displayed
        Assert.Equal(string.Empty, dialog!.TemporaryPassword); // and the typed values are gone
        Assert.Equal(string.Empty, dialog.ConfirmPassword);
        Assert.DoesNotContain(typeof(StaffViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(string));
    }

    [Fact]
    public async Task ResetPassword_DemoOrServerRefusal_StaysInTheDialogWithAPlainMessage()
    {
        var rig = new Part2Rig();
        rig.Fake.OnVoid("ResetStaffPasswordAsync", ApiResult.Fail(new ApiProblem(403, ProblemCodes.DemoForbidden, "forbidden")));
        var dialog = new ResetPasswordDialogViewModel(rig.Api, 2, "Staff 002") { TemporaryPassword = "Temporary-Pass-12", ConfirmPassword = "Temporary-Pass-12" };

        await dialog.PrimaryCommand.ExecuteAsync();

        Assert.False(dialog.Completed);
        Assert.Equal("The demo account can't make this change.", dialog.FormError);
        Assert.False(dialog.IsBusy);
        Assert.Equal("Temporary-Pass-12", dialog.TemporaryPassword); // what was typed is kept so it can be corrected
    }

    // FEAT-24, FEAT-26

    private static async Task<(Part2Rig Rig, ProfileViewModel Vm)> LoadedProfileAsync(bool hasPhoto = false, IPhotoPicker? picker = null, IChangePasswordPrompt? prompt = null)
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile(hasPhoto)));
        rig.Fake.On("GetMyPhotoAsync", ApiResult<byte[]>.Ok([9, 9, 9]));
        var vm = rig.Profile(picker, prompt);
        await vm.LoadAsync();
        return (rig, vm);
    }

    [Fact]
    public async Task Profile_SaveEnabledOnlyWhenChanged()
    {
        var (_, vm) = await LoadedProfileAsync();

        Assert.Equal("Test Admin", vm.DisplayName);
        Assert.Equal("admin", vm.Username);
        Assert.Equal("Admin", vm.DesignationText);
        Assert.Equal("Full time", vm.ShiftText);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.DisplayName = "Dr. Test Admin";
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.DisplayName = "Test Admin"; // back to what it was
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Phone = "  +1-555-0101  "; // spaces around the same value are not a change
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Profile_SendsPhotoOnlyWhenReplaced()
    {
        var picker = new FakePhotoPicker([7, 7, 7]);
        var (rig, vm) = await LoadedProfileAsync(hasPhoto: true, picker);
        rig.Fake.On("UpdateProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile(true) with { DisplayName = "Dr. Test Admin", RowVersion = "rv-p2" }));
        rig.Fake.OnVoid("SetMyPhotoAsync", ApiResult.Success);
        Assert.Equal([9, 9, 9], vm.PhotoBytes);

        // Only the name changed: the profile is sent, the photo is not.
        vm.DisplayName = "Dr. Test Admin";
        await vm.SaveCommand.ExecuteAsync();
        Assert.Equal(1, rig.Recorder.CountOf("UpdateProfileAsync"));
        Assert.Equal(0, rig.Recorder.CountOf("SetMyPhotoAsync"));
        var sent = rig.Recorder.LastArg<UpdateProfileRequest>("UpdateProfileAsync");
        Assert.True(sent.DisplayName.HasValue);
        Assert.False(sent.Phone.HasValue);
        Assert.Equal("rv-p1", sent.RowVersion);

        // Only the photo was replaced: the photo is sent, the profile is not.
        await vm.PickPhotoCommand.ExecuteAsync();
        Assert.True(vm.PhotoWasReplaced);
        Assert.Equal([7, 7, 7], vm.PhotoBytes);
        Assert.True(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync();
        Assert.Equal(1, rig.Recorder.CountOf("UpdateProfileAsync"));
        Assert.Equal(1, rig.Recorder.CountOf("SetMyPhotoAsync"));
        Assert.Equal([7, 7, 7], (byte[])rig.Recorder.Calls.Single(c => c.Name == "SetMyPhotoAsync").Args[0]!);
        Assert.False(vm.PhotoWasReplaced);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // A later name change does not send the photo again.
        vm.DisplayName = "Someone Else";
        await vm.SaveCommand.ExecuteAsync();
        Assert.Equal(2, rig.Recorder.CountOf("UpdateProfileAsync"));
        Assert.Equal(1, rig.Recorder.CountOf("SetMyPhotoAsync"));
        Assert.Equal("rv-p2", rig.Recorder.LastArg<UpdateProfileRequest>("UpdateProfileAsync").RowVersion);
    }

    [Fact]
    public async Task Profile_PhotoRules_CancelledPickerChangesNothing_OversizedIsRefused()
    {
        var picker = new FakePhotoPicker(null);
        var (_, vm) = await LoadedProfileAsync(false, picker);

        await vm.PickPhotoCommand.ExecuteAsync(); // cancelled
        Assert.False(vm.PhotoWasReplaced);
        Assert.False(vm.HasUnsavedChanges);

        picker.Bytes = new byte[ProfileViewModel.MaxPhotoBytes + 1];
        await vm.PickPhotoCommand.ExecuteAsync();
        Assert.False(vm.PhotoWasReplaced);
        Assert.Contains("larger than 1 MB", vm.FormError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_ClearingThePhone_SendsAnExplicitNull_AndInvalidInputIsRefusedBeforeSending()
    {
        var (rig, vm) = await LoadedProfileAsync();
        rig.Fake.On("UpdateProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile() with { Phone = null }));

        vm.Phone = "abc";
        await vm.SaveCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("UpdateProfileAsync"));
        Assert.NotNull(vm["Phone"]);

        vm.Phone = string.Empty;
        vm.DisplayName = string.Empty;
        await vm.SaveCommand.ExecuteAsync();
        Assert.Equal("Enter your name.", vm["DisplayName"]);

        vm.DisplayName = "Test Admin";
        await vm.SaveCommand.ExecuteAsync();
        var sent = rig.Recorder.LastArg<UpdateProfileRequest>("UpdateProfileAsync");
        Assert.True(sent.Phone.HasValue);
        Assert.Null(sent.Phone.Value);
        Assert.False(sent.DisplayName.HasValue);
    }

    [Fact]
    public async Task Profile_On409_ShowsReload_NeverOverwrites()
    {
        var (rig, vm) = await LoadedProfileAsync();
        rig.Fake.On("UpdateProfileAsync", FakeApiClient.Fail<ProfileResponse>(409, ProblemCodes.Concurrency, "changed"));
        vm.DisplayName = "Mine";

        await vm.SaveCommand.ExecuteAsync();

        Assert.True(vm.HasConflict);
        Assert.Empty(rig.Toasts.Toasts);
        await vm.ReloadCommand.ExecuteAsync();
        Assert.False(vm.HasConflict);
        Assert.Equal("Test Admin", vm.DisplayName);
    }

    [Fact]
    public async Task Profile_WithUnsavedChanges_AsksDiscardChanges_WhenNavigatingAway()
    {
        using var rig = new Rig();
        rig.Api.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        rig.Api.On("GetDonorsAsync", ApiResult<PagedResult<Lifestream.Contracts.Donors.DonorListItem>>.Ok(Samples.DonorPage(1)));
        var asked = new List<string>();
        rig.Get<DialogService>().Presenter = new AskingPresenter(asked, answer: false);
        var navigation = rig.Get<INavigationService>();
        Assert.True(await navigation.NavigateTo<ProfileViewModel>());
        var profile = (ProfileViewModel)navigation.Current!;

        Assert.True(await navigation.NavigateTo<Lifestream.Client.Features.Donors.DonorsViewModel>()); // nothing changed: leaving is free
        Assert.True(await navigation.NavigateTo<ProfileViewModel>());
        profile = (ProfileViewModel)navigation.Current!;
        profile.DisplayName = "Changed";

        Assert.False(await navigation.NavigateTo<Lifestream.Client.Features.Donors.DonorsViewModel>()); // the user chose to stay
        Assert.Equal(["Discard changes?"], asked);
        Assert.Same(profile, navigation.Current);
    }

    private sealed class AskingPresenter(List<string> asked, bool answer) : IDialogPresenter
    {
        public Task<bool> ConfirmAsync(string title, string body, string dangerVerb)
        {
            asked.Add(title);
            return Task.FromResult(answer);
        }

        public Task ShowErrorAsync(string title, string message, string? details) => Task.CompletedTask;
    }

    // FEAT-25

    [Fact]
    public async Task ChangePassword_Dialog_WorksWithAndWithoutPhoto()
    {
        foreach (var hasPhoto in new[] { false, true })
        {
            var rig = new Part2Rig();
            rig.Fake.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile(hasPhoto)));
            rig.Fake.On("GetMyPhotoAsync", ApiResult<byte[]>.Ok([1]));
            rig.Fake.OnVoid("ChangePasswordAsync", ApiResult.Success);
            var profile = rig.Profile(prompt: new ChangePasswordPrompt(rig.Api, rig.Forms, rig.Toasts));
            await profile.LoadAsync();
            Assert.Equal(hasPhoto ? 1 : 0, rig.Recorder.CountOf("GetMyPhotoAsync")); // a user without a photo has none to fetch

            rig.Forms.Script = async dialog =>
            {
                var change = Assert.IsType<ChangePasswordDialogViewModel>(dialog);
                change.CurrentPassword = "the-old-password";
                change.NewPassword = "the-new-password-1";
                change.ConfirmPassword = "the-new-password-1";
                await change.PrimaryCommand.ExecuteAsync();
            };
            await profile.ChangePasswordCommand.ExecuteAsync();

            var sent = rig.Recorder.LastArg<ChangePasswordRequest>("ChangePasswordAsync");
            Assert.Equal(("the-old-password", "the-new-password-1"), (sent.CurrentPassword, sent.NewPassword));
            Assert.Equal(0, rig.Recorder.CountOf("SetMyPhotoAsync")); // the password change never touches the photo
            Assert.Equal(0, rig.Recorder.CountOf("UpdateProfileAsync"));
            Assert.Equal(hasPhoto ? 1 : 0, rig.Recorder.CountOf("GetMyPhotoAsync"));
            Assert.Equal("Your password was changed.", Assert.Single(rig.Toasts.Toasts).Message);
        }
    }

    [Fact]
    public async Task ChangePassword_ValidatesCurrentNewAndConfirm_AndPutsServerMessagesUnderTheField()
    {
        var rig = new Part2Rig();
        var dialog = new ChangePasswordDialogViewModel(rig.Api);

        await dialog.PrimaryCommand.ExecuteAsync();
        Assert.Equal("Enter your current password.", dialog.GetError(nameof(ChangePasswordDialogViewModel.CurrentPassword)));
        Assert.Equal("Enter a new password.", dialog.GetError(nameof(ChangePasswordDialogViewModel.NewPassword)));
        Assert.Equal("Type the new password again.", dialog.GetError(nameof(ChangePasswordDialogViewModel.ConfirmPassword)));

        dialog.CurrentPassword = "same-password-12";
        dialog.NewPassword = "same-password-12";
        dialog.ConfirmPassword = "other";
        await dialog.PrimaryCommand.ExecuteAsync();
        Assert.Equal("The new password must be different from the current one.", dialog.GetError(nameof(ChangePasswordDialogViewModel.NewPassword)));
        Assert.Equal("The two passwords don't match.", dialog.GetError(nameof(ChangePasswordDialogViewModel.ConfirmPassword)));
        Assert.Equal(0, rig.Recorder.CountOf("ChangePasswordAsync"));

        dialog.NewPassword = "brand-new-password";
        dialog.ConfirmPassword = "brand-new-password";
        rig.Fake.OnVoid("ChangePasswordAsync", ApiResult.Fail(new ApiProblem(400, ProblemCodes.Validation, "Validation failed", "c", "{\"errors\":{\"currentPassword\":[\"is not correct\"]}}")));
        await dialog.PrimaryCommand.ExecuteAsync();

        Assert.Equal("Your current password isn't correct.", dialog.GetError(nameof(ChangePasswordDialogViewModel.CurrentPassword)));
        Assert.False(dialog.Completed);
        Assert.False(dialog.IsBusy);
    }

    [Fact]
    public async Task ChangePassword_ParameterFromTheAccountMenu_OpensTheDialogAfterTheProfileLoads()
    {
        var prompt = new FakePasswordPrompt();
        var rig = new Part2Rig();
        rig.Fake.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(Samples2.Profile()));
        var vm = rig.Profile(prompt: prompt);
        vm.ReceiveParameter(ProfileViewModel.ChangePasswordParameter);

        await vm.LoadAsync();

        Assert.Equal([false], prompt.Prompts);
    }

    [Fact]
    public async Task ChangePasswordPrompt_RequiredMode_ExplainsWhy_AndOffersNotNow()
    {
        var rig = new Part2Rig();
        rig.Forms.Script = dialog =>
        {
            var change = Assert.IsType<ChangePasswordDialogViewModel>(dialog);
            Assert.True(change.IsRequired);
            Assert.Contains("administrator reset your password", change.Explanation, StringComparison.Ordinal);
            Assert.Equal("Not now", change.CancelText);
            return change.CancelCommand.ExecuteAsync();
        };
        var prompt = new ChangePasswordPrompt(rig.Api, rig.Forms, rig.Toasts);

        var changed = await prompt.PromptAsync(required: true);

        Assert.False(changed);
        Assert.Empty(rig.Toasts.Toasts);
    }
}
