using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lifestream.Client.Common;

/// <summary>Which colour theme the app uses.</summary>
public enum ThemeMode
{
    /// <summary>Follow the Windows app mode (light or dark), and change with it.</summary>
    System,
    Light,
    Dark,
}

/// <summary>How much room rows and controls take.</summary>
public enum Density
{
    Comfortable,
    Compact,
}

/// <summary>
/// The person's display choices on this PC: theme, density, the collapsed sidebar and the remembered user name. Every change is saved at once
/// through the store; nothing here is secret (no password or token is ever kept).
/// </summary>
public sealed partial class UiPreferences : ObservableObject
{
    private readonly IPreferencesStore _store;
    private bool _loading;

    public UiPreferences(IPreferencesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _loading = true;
        try
        {
            var saved = store.Load();
            Theme = saved.Theme;
            Density = saved.Density;
            IsNavCollapsed = saved.IsNavCollapsed;
            RememberUsername = saved.RememberUsername;
            Username = saved.RememberUsername ? saved.Username : null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Light, dark or follow Windows (the default).</summary>
    [ObservableProperty]
    public partial ThemeMode Theme { get; set; }

    [ObservableProperty]
    public partial Density Density { get; set; }

    /// <summary>The sidebar is an icon rail by choice (Ctrl+B). A compact window shows the rail whatever this says.</summary>
    [ObservableProperty]
    public partial bool IsNavCollapsed { get; set; }

    /// <summary>Fill in the last user name on the sign-in screen.</summary>
    [ObservableProperty]
    public partial bool RememberUsername { get; set; }

    /// <summary>The last user name that signed in, when <see cref="RememberUsername"/> is on.</summary>
    [ObservableProperty]
    public partial string? Username { get; set; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading)
        {
            _store.Save(new PreferencesData(Theme, Density, IsNavCollapsed, RememberUsername, RememberUsername ? Username : null));
        }
    }
}

/// <summary>The saved form of <see cref="UiPreferences"/>.</summary>
public sealed record PreferencesData(ThemeMode Theme = ThemeMode.System, Density Density = Density.Comfortable, bool IsNavCollapsed = false, bool RememberUsername = false, string? Username = null);

/// <summary>Where preferences live.</summary>
public interface IPreferencesStore
{
    PreferencesData Load();

    void Save(PreferencesData data);
}

/// <summary>Keeps preferences in memory only (tests, and the fallback when the file cannot be used).</summary>
public sealed class MemoryPreferencesStore(PreferencesData? initial = null) : IPreferencesStore
{
    public PreferencesData Data { get; private set; } = initial ?? new PreferencesData();

    public PreferencesData Load() => Data;

    public void Save(PreferencesData data) => Data = data;
}

/// <summary>
/// Preferences as a small JSON file (by default %LOCALAPPDATA%\Lifestream\preferences.json). A missing, unreadable or corrupt file means the
/// defaults; a failed save is ignored, because a display preference is never worth an error dialog.
/// </summary>
public sealed class FilePreferencesStore(string path) : IPreferencesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lifestream", "preferences.json");

    public PreferencesData Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PreferencesData>(File.ReadAllText(path), Json) ?? new PreferencesData() : new PreferencesData();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new PreferencesData();
        }
    }

    public void Save(PreferencesData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(data, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that cannot be written is simply not remembered.
        }
    }
}
