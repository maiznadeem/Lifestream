using System.Text.RegularExpressions;
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
    public const int MaxPhone = 20;

    /// <summary>The navigation parameter the account menu's "Change password" sends: the screen opens with the dialog already open.</summary>
    public const string ChangePasswordParameter = "change-password";

    /// <summary>The largest photo the API accepts.</summary>
    public const int MaxPhotoBytes = 1024 * 1024;

    private static readonly Regex PhonePattern = new(@"^\+?[0-9][0-9 ()\-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly ILifestreamApiClient _api;
    private readonly IToastService _toasts;
    private readonly IPhotoPicker _photos;
    private readonly IChangePasswordPrompt _passwords;
    private ProfileResponse? _fetched;
    private byte[]? _fetchedPhoto;
    private string _rowVersion = string.Empty;
    private string _baselineName = string.Empty;
    private string _baselinePhone = string.Empty;
    private byte[]? _replacedPhoto;
    private bool _openPasswordDialog;

    public ProfileViewModel(ILifestreamApiClient api, IToastService toasts, IPhotoPicker photos, IChangePasswordPrompt passwords)
    {
        _api = api;
        _toasts = toasts;
        _photos = photos;
        _passwords = passwords;
        DeclareFields(nameof(DisplayName), nameof(Phone));
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasUnsavedChanges && !IsBusy);
        DiscardCommand = new AsyncRelayCommand(_ =>
        {
            DisplayName = _baselineName;
            Phone = _baselinePhone;
            _replacedPhoto = null;
            PhotoBytes = _fetchedPhoto;
            ChangesChanged();
            return Task.CompletedTask;
        }, () => HasUnsavedChanges && !IsBusy);
        PickPhotoCommand = new AsyncRelayCommand(PickPhotoAsync, () => !IsBusy);
        ChangePasswordCommand = new AsyncRelayCommand(_ => _passwords.PromptAsync(), () => !IsBusy);
        RetryCommand = new AsyncRelayCommand(ct => LoadAsync(ct));
        ReloadCommand = new AsyncRelayCommand(ct => LoadAsync(ct));
        ErrorsChanged += (_, _) => OnPropertyChanged("Item[]");
    }

    public override string PageTitle => "Profile";

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand DiscardCommand { get; }

    public AsyncRelayCommand PickPhotoCommand { get; }

    public AsyncRelayCommand ChangePasswordCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>The message of a field, or null; bound by the view as <c>{Binding [DisplayName]}</c>.</summary>
    public string? this[string field] => GetError(field);

    public string SaveText => "Save changes";

    public string SaveBusyText => "Saving...";

    // Read-only data, shown as label and value text.
    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DesignationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShiftText { get; set; } = string.Empty;

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

    public override bool HasUnsavedChanges => IsDirty;

    private bool IsDirty => State == LoadState.Ready && (DisplayName.Trim() != _baselineName || NormalisePhone(Phone) != _baselinePhone || _replacedPhoto is not null);

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
        DesignationText = profile.Designation.ToString();
        ShiftText = StaffText.Shift(profile.Shift);
        _baselineName = profile.DisplayName.Trim();
        _baselinePhone = profile.Phone?.Trim() ?? string.Empty;
        DisplayName = _baselineName;
        Phone = _baselinePhone;
        _replacedPhoto = null;
        PhotoBytes = _fetchedPhoto;
        HasConflict = false;
        FormError = null;
        ChangesChanged();
        if (_openPasswordDialog)
        {
            _openPasswordDialog = false;
            ChangePasswordCommand.Execute(null);
        }
    }

    protected override void OnBusyChanged(bool isBusy) => RaiseCommandStates();

    // ---- editing ----------------------------------------------------------------------------------------------------

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

                if (phone.Length > MaxPhone)
                {
                    return $"The phone number can be at most {MaxPhone} characters.";
                }

                return PhonePattern.IsMatch(phone) ? null : "Use digits only (a leading + and spaces, dashes or brackets are allowed).";
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
        await RunBusyAsync(async () =>
        {
            problem = await Task.Run(async () =>
            {
                if (update is not null)
                {
                    var result = await _api.UpdateProfileAsync(update, ct).ConfigureAwait(false);
                    if (!result.IsOk)
                    {
                        return result.Problem;
                    }

                    saved = result.Value;
                }

                if (photo is not null)
                {
                    var result = await _api.SetMyPhotoAsync(photo, ct).ConfigureAwait(false);
                    if (!result.IsOk)
                    {
                        return result.Problem;
                    }

                    photoSaved = true;
                }

                return null;
            }, ct).ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (saved is { } profile)
        {
            _rowVersion = profile.RowVersion;
            _baselineName = profile.DisplayName.Trim();
            _baselinePhone = profile.Phone?.Trim() ?? string.Empty;
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
            foreach (var (serverField, message) in ProblemFieldErrors.Read(problem))
            {
                var field = serverField.ToLowerInvariant() switch
                {
                    "displayname" => nameof(DisplayName),
                    "phone" => nameof(Phone),
                    _ => null,
                };
                if (field is not null)
                {
                    SetError(field, message);
                    return;
                }
            }
        }

        FormError = ProblemText.For(problem, "profile");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _replacedPhoto = null;
        }

        base.Dispose(disposing);
    }
}
