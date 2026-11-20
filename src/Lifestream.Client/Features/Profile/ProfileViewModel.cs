using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Validation;
using Lifestream.Contracts;
using Lifestream.Contracts.Profile;

namespace Lifestream.Client.Features.Profile;

/// <summary>Lets the user choose a photo file. The Desktop project shows the file dialog and returns a JPEG of at most 256 pixels; tests inject a fake.</summary>
public interface IPhotoPicker
{
    /// <returns>The JPEG bytes of the chosen photo, or null when the user cancelled.</returns>
    Task<byte[]?> PickAsync();
}

/// <summary>Used when the app registered no picker (headless hosts): the user can't choose a photo.</summary>
public sealed class NoPhotoPicker : IPhotoPicker
{
    public Task<byte[]?> PickAsync() => Task.FromResult<byte[]?>(null);
}

/// <summary>
/// The Profile screen (FEAT-24, FEAT-26). What can't be changed (username, designation, shift) is label and value text; the display name, phone and
/// photo can. Save is enabled only when something changed, and sends only what changed: the photo goes to the API only when the user replaced it,
/// never as a side effect of saving the name. Leaving with unsaved changes asks "Discard changes?". The password is changed in its own dialog.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML, which needs instance properties.")]
public sealed partial class ProfileViewModel : FormViewModelBase, INavigationParameterReceiver
{
    public const int MaxName = 100;

    /// <summary>The navigation parameter the account menu's "Change password" sends: the screen opens with the dialog already open.</summary>
    public const string ChangePasswordParameter = "change-password";

    /// <summary>The largest photo the API accepts.</summary>
    public const int MaxPhotoBytes = 1024 * 1024;


    private readonly ILifestreamApiClient _api;
    private readonly IToastService _toasts;
    private readonly IPhotoPicker _photos;

    protected override IToastService? SessionToasts => _toasts;
    private readonly IChangePasswordPrompt _passwords;
    private readonly UiPreferences? _preferences;
    private readonly Session.ISessionService? _session;
    private ProfileResponse? _fetched;
    private byte[]? _fetchedPhoto;
    private string _rowVersion = string.Empty;
    private string _baselineName = string.Empty;
    private string _baselinePhone = string.Empty;
    private byte[]? _replacedPhoto;
    private bool _openPasswordDialog;

    /// <summary>
    /// Creates the screen; <paramref name="preferences"/> holds the appearance choice (light, dark or match Windows), which the app applies at once (without it
    /// the Appearance section is hidden); <paramref name="session"/> says whether this is the shared demo account, whose password cannot be changed.
    /// </summary>
    public ProfileViewModel(ILifestreamApiClient api, IToastService toasts, IPhotoPicker photos, IChangePasswordPrompt passwords, UiPreferences? preferences = null, Session.ISessionService? session = null)
    {
        _api = api;
        _preferences = preferences;
        _session = session;
        if (preferences is not null)
        {
            preferences.PropertyChanged += OnPreferencesChanged;
        }

        _toasts = toasts;
        _photos = photos;
        _passwords = passwords;
        DeclareFields(nameof(DisplayName), nameof(Phone));
        SaveCommand = Command(SaveAsync, () => CanEditProfile && HasUnsavedChanges && !IsBusy);
        DiscardCommand = Command(_ =>
        {
            DisplayName = _baselineName;
            Phone = _baselinePhone;
            _replacedPhoto = null;
            PhotoBytes = _fetchedPhoto;
            ChangesChanged();
            return Task.CompletedTask;
        }, () => HasUnsavedChanges && !IsBusy);
        PickPhotoCommand = Command(PickPhotoAsync, () => CanEditProfile && !IsBusy, opensModal: true);
        ChangePasswordCommand = Command(_ => _passwords.PromptAsync(), () => CanChangePassword && !IsBusy, opensModal: true);
        RetryCommand = Command(ct => LoadAsync(ct));
        ReloadCommand = Command(ct => LoadAsync(ct));
        ErrorsChanged += (_, _) => OnPropertyChanged("Item[]");
    }

    public override string PageTitle => "Profile";

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand DiscardCommand { get; }

    public AsyncRelayCommand PickPhotoCommand { get; }

    public AsyncRelayCommand ChangePasswordCommand { get; }

    /// <summary>
    /// False for the demo account: everyone who tries the demo shares it, so the server refuses to change its password. The screen then says so
    /// instead of offering a dialog that could only fail.
    /// </summary>
    public bool CanChangePassword => _session?.User?.IsDemo != true;

    /// <summary>
    /// False for the demo account: it is shared, and the server refuses a change to its name, phone or photo. The screen is then read-only (the
    /// name and phone cannot be typed in, there is no "Change photo", nothing is ever unsaved) and says why in one line, instead of letting the
    /// person edit and then fail on Save.
    /// </summary>
    public bool CanEditProfile => _session?.User?.IsDemo != true;

    /// <summary>True for the demo account: the name and phone boxes are read-only.</summary>
    public bool IsProfileReadOnly => !CanEditProfile;

    /// <summary>The line under "Your details": who sees them, or why they cannot be changed.</summary>
    public string DetailsSectionText => CanEditProfile
        ? "Your name and phone show to the rest of the team."
        : "The demo account is shared by everyone who tries Lifestream, so its name, phone and photo can't be changed.";

    /// <summary>What the Security section says instead of offering the button when the password cannot be changed.</summary>
    public string PasswordUnavailableText => "The demo account is shared by everyone who tries Lifestream, so its password can't be changed.";

    /// <summary>The line under the Security section's heading: what the button does, or why there is none.</summary>
    public string PasswordSectionText => CanChangePassword ? "Change the password you sign in with. You'll need the current one." : PasswordUnavailableText;

    public AsyncRelayCommand RetryCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>The message of a field, or null; bound by the view as <c>{Binding [DisplayName]}</c>.</summary>
    public string? this[string field] => GetError(field);

    public string SaveText => "Save changes";

    /// <summary>The keyboard shortcuts of the main window, for the Shortcuts section (the same keys the shell binds).</summary>
    public IReadOnlyList<ShortcutInfo> Shortcuts { get; } =
    [
        new(["Ctrl", "K"], "Search records or run a command"),
        new(["Ctrl", "F"], "Search the list on this page"),
        new(["Ctrl", "N"], "Add a record on this page"),
        new(["Ctrl", "D"], "Record a donation for the next appointment today"),
        new(["Ctrl", "B"], "Collapse or expand the sidebar"),
        new(["Alt", "1–8"], "Open a page of the sidebar"),
        new(["F6"], "Move between the title bar, the sidebar and the page"),
        new(["Esc"], "Close the side panel, the dialog or the palette"),
    ];

    public string SaveBusyText => "Saving...";

    private const string NotRecorded = "Not recorded";

    // Read-only data, shown as label and value text.
    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DesignationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShiftText { get; set; } = string.Empty;

    /// <summary>The person's date of birth, in the app's date format ("17 May 1990").</summary>
    [ObservableProperty]
    public partial string DateOfBirthText { get; set; } = string.Empty;

    /// <summary>The day the account was created ("Member since"), in the app's date format and the user's own time zone.</summary>
    [ObservableProperty]
    public partial string MemberSinceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Phone { get; set; } = string.Empty;

    /// <summary>The photo shown: the stored one, or the one just chosen. Null shows the shared default avatar.</summary>
    [ObservableProperty]
    public partial byte[]? PhotoBytes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    public partial string? FormError { get; set; }

    public bool HasFormError => !string.IsNullOrEmpty(FormError);

    /// <summary>True after a 409: someone else changed the profile. Reload shows the latest and never overwrites.</summary>
    [ObservableProperty]
    public partial bool HasConflict { get; set; }

    public string ConflictMessage => "This profile was changed somewhere else while you were editing. Reload to see the latest version, then make your change again.";

    public bool PhotoWasReplaced => _replacedPhoto is not null;

    /// <summary>The account header's name: the saved one, so an unsaved edit of the name field does not show there before Save (design-qa-2 N18).</summary>
    public string SavedName => _baselineName;

    /// <summary>The avatar's letters when there is no photo, from the saved name like the header beside it.</summary>
    public string Initials => Staff.StaffText.Initials(_baselineName);

    /// <summary>True when the avatar shows a photo (the stored one or a new one).</summary>
    public bool HasPhoto => PhotoBytes is { Length: > 0 };

    // ---- appearance -------------------------------------------------------------------------------------------------

    /// <summary>True when this screen can change the appearance (the app gave it the preferences).</summary>
    public bool CanChooseTheme => _preferences is not null;

    /// <summary>The light theme.</summary>
    public bool IsLightTheme
    {
        get => _preferences?.Theme == ThemeMode.Light;
        set => SetTheme(value, ThemeMode.Light, nameof(IsLightTheme));
    }

    /// <summary>The dark theme.</summary>
    public bool IsDarkTheme
    {
        get => _preferences?.Theme == ThemeMode.Dark;
        set => SetTheme(value, ThemeMode.Dark, nameof(IsDarkTheme));
    }

    /// <summary>Follow the Windows app mode (the default).</summary>
    public bool IsSystemTheme
    {
        get => _preferences is null || _preferences.Theme == ThemeMode.System;
        set => SetTheme(value, ThemeMode.System, nameof(IsSystemTheme));
    }

    private void SetTheme(bool chosen, ThemeMode mode, string option)
    {
        if (chosen && _preferences is not null && _preferences.Theme != mode)
        {
            _preferences.Theme = mode; // saved at once; the app swaps the colours of every open window
        }
        else
        {
            OnPropertyChanged(option); // unchecked by its group, or nothing to change: re-read
        }
    }

    private void OnPreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UiPreferences.Theme))
        {
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(IsSystemTheme));
        }
    }

    public override bool HasUnsavedChanges => IsDirty;

    private bool IsDirty => CanEditProfile && State == LoadState.Ready && (DisplayName.Trim() != _baselineName || NormalisePhone(Phone) != _baselinePhone || _replacedPhoto is not null);

    private static string NormalisePhone(string phone) => phone.Trim();

    public void ReceiveParameter(object parameter)
    {
        if (parameter is string text && string.Equals(text, ChangePasswordParameter, StringComparison.Ordinal))
        {
            _openPasswordDialog = true;
        }
    }

    // ---- loading ----------------------------------------------------------------------------------------------------

    protected override async Task FetchAsync(CancellationToken ct)
    {
        _fetched = (await _api.GetProfileAsync(ct).ConfigureAwait(false)).Unwrap();
        _fetchedPhoto = null;
        if (_fetched.HasPhoto)
        {
            // Only a user with a stored photo has one to fetch; the others show the default avatar and make no photo call.
            var photo = await _api.GetMyPhotoAsync(ct).ConfigureAwait(false);
            _fetchedPhoto = photo.IsOk ? photo.Value : null;
        }
    }

    protected override void Apply()
    {
        if (_fetched is not { } profile)
        {
            return;
        }

        _rowVersion = profile.RowVersion;
        Username = profile.Username;
        DesignationText = StaffText.JobTitle(profile.Designation);
        ShiftText = StaffText.Shift(profile.Shift);
        DateOfBirthText = profile.DateOfBirth == default ? NotRecorded : Formatting.Date(profile.DateOfBirth);
        MemberSinceText = profile.MemberSinceUtc == default ? NotRecorded : Formatting.Date(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(profile.MemberSinceUtc, DateTimeKind.Utc), TimeZoneInfo.Local)));
        _baselineName = profile.DisplayName.Trim();
        _baselinePhone = profile.Phone?.Trim() ?? string.Empty;
        OnPropertyChanged(nameof(SavedName));
        OnPropertyChanged(nameof(Initials));
        DisplayName = _baselineName;
        Phone = _baselinePhone;
        _replacedPhoto = null;
        PhotoBytes = _fetchedPhoto;
        HasConflict = false;
        FormError = null;
        ChangesChanged();
    }

    /// <summary>The account menu's "Change password" opens the dialog once the profile is on screen: after the load, never during it.</summary>
    protected override void OnLoaded()
    {
        if (_openPasswordDialog)
        {
            _openPasswordDialog = false;
            if (ChangePasswordCommand.CanExecute(null))
            {
                ChangePasswordCommand.Execute(null);
            }
        }
    }

    protected override void OnBusyChanged(bool isBusy) => RaiseCommandStates();

    // ---- editing ----------------------------------------------------------------------------------------------------

    partial void OnPhotoBytesChanged(byte[]? value) => OnPropertyChanged(nameof(HasPhoto));

    partial void OnDisplayNameChanged(string value)
    {
        ClearError(nameof(DisplayName));
        FormError = null;
        ChangesChanged();
    }

    partial void OnPhoneChanged(string value)
    {
        ClearError(nameof(Phone));
        FormError = null;
        ChangesChanged();
    }

    private void ChangesChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(PhotoWasReplaced));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        PickPhotoCommand.RaiseCanExecuteChanged();
        ChangePasswordCommand.RaiseCanExecuteChanged();
    }

    protected override string? ValidateField(string field)
    {
        switch (field)
        {
            case nameof(DisplayName):
                if (string.IsNullOrWhiteSpace(DisplayName))
                {
                    return "Enter your name.";
                }

                return DisplayName.Trim().Length > MaxName ? $"The name can be at most {MaxName} characters." : null;
            case nameof(Phone):
                var phone = Phone.Trim();
                if (phone.Length == 0)
                {
                    return null;
                }

                return PhoneRule.Validate(phone);
            default:
                return null;
        }
    }

    private async Task PickPhotoAsync(CancellationToken ct)
    {
        FormError = null;
        var bytes = await _photos.PickAsync().ConfigureAwait(true);
        if (bytes is null || bytes.Length == 0)
        {
            return; // cancelled
        }

        if (bytes.Length > MaxPhotoBytes)
        {
            FormError = "That photo is larger than 1 MB. Choose a smaller one.";
            return;
        }

        _replacedPhoto = bytes;
        PhotoBytes = bytes;
        ChangesChanged();
    }

    // ---- saving -----------------------------------------------------------------------------------------------------

    /// <summary>The PATCH body: the row version and only the text fields that changed (an emptied phone is an explicit null), or null when no text changed.</summary>
    public UpdateProfileRequest? BuildUpdate()
    {
        var name = DisplayName.Trim();
        var phone = NormalisePhone(Phone);
        var nameChanged = name != _baselineName;
        var phoneChanged = phone != _baselinePhone;
        if (!nameChanged && !phoneChanged)
        {
            return null;
        }

        return new UpdateProfileRequest(
            _rowVersion,
            nameChanged ? Optional.Of(name) : default,
            phoneChanged ? Optional.Of<string?>(phone.Length == 0 ? null : phone) : default);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        FormError = null;
        HasConflict = false;
        if (!ValidateAll())
        {
            return;
        }

        var update = BuildUpdate();
        var photo = _replacedPhoto;
        ApiProblem? problem = null;
        ProfileResponse? saved = null;
        var photoSaved = false;
        var cancelled = false;
        await RunBusyAsync(async () =>
        {
            try
            {
                problem = await RunOperationAsync(async token =>
                {
                    if (update is not null)
                    {
                        var result = await _api.UpdateProfileAsync(update, token).ConfigureAwait(false);
                        if (!result.IsOk)
                        {
                            return result.Problem;
                        }

                        saved = result.Value;
                    }

                    if (photo is not null)
                    {
                        var result = await _api.SetMyPhotoAsync(photo, token).ConfigureAwait(false);
                        if (!result.IsOk)
                        {
                            return result.Problem;
                        }

                        photoSaved = true;
                    }

                    return null;
                }, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }).ConfigureAwait(true);

        if (cancelled)
        {
            FormError = "The request was cancelled. Your changes are still here, so you can save again.";
            return;
        }

        if (saved is { } profile)
        {
            _rowVersion = profile.RowVersion;
            _baselineName = profile.DisplayName.Trim();
            _baselinePhone = profile.Phone?.Trim() ?? string.Empty;
            OnPropertyChanged(nameof(SavedName));
            OnPropertyChanged(nameof(Initials));
        }

        if (photoSaved)
        {
            _fetchedPhoto = photo;
            _replacedPhoto = null;
        }

        ChangesChanged();
        if (problem is null)
        {
            _toasts.Show(ToastKind.Success, "Your profile was saved.");
            return;
        }

        ShowProblem(problem);
    }

    private void ShowProblem(ApiProblem problem)
    {
        if (problem.Code == ProblemCodes.Concurrency)
        {
            HasConflict = true;
            return;
        }

        if (problem.Code == ProblemCodes.Validation)
        {
            if (problem.ErrorsFor("displayName") is [{ } nameMessage, ..])
            {
                SetError(nameof(DisplayName), nameMessage);
                return;
            }

            if (problem.ErrorsFor("phone") is [{ } phoneMessage, ..])
            {
                SetError(nameof(Phone), phoneMessage);
                return;
            }
        }

        FormError = ProblemText.For(problem, "profile");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _replacedPhoto = null;
            if (_preferences is not null)
            {
                _preferences.PropertyChanged -= OnPreferencesChanged;
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>One keyboard shortcut: its keys ("Ctrl", "K") and what it does.</summary>
public sealed record ShortcutInfo(IReadOnlyList<string> Keys, string Action)
{
    public string AutomationName => $"{string.Join(" ", Keys)}: {Action}";
}
