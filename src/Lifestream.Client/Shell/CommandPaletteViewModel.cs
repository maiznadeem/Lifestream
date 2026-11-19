using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;

namespace Lifestream.Client.Shell;

/// <summary>One row of the command palette: what it does, where it is grouped, and the shortcut that does the same.</summary>
public sealed record PaletteItem(string Group, string Title, string? Subtitle, string Icon, string? Shortcut, Func<Task> Run)
{
    /// <summary>"Record donation, Ctrl+D" for screen readers.</summary>
    public string AutomationName => string.Join(", ", new[] { Title, Subtitle, Shortcut }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// The command palette (Ctrl+K, docs/design-system.md "Command palette"): go to any screen, run the primary actions, change the theme, and find donors,
/// patients and requests through the API's record search (debounced 150 ms). Results are grouped; Up and Down move, Enter runs, Esc closes. With an empty
/// query it shows what was run recently, then the actions and screens. Each action shows its shortcut.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    /// <summary>How long typing must pause before the server is searched.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    private const int MaxRecent = 5;
    private const int MaxPerKind = 5;
    private readonly ShellViewModel _shell;
    private readonly ILifestreamApiClient? _api;
    private readonly TimeProvider _clock;
    private readonly List<PaletteItem> _recent = [];
    private CancellationTokenSource? _search;

    public CommandPaletteViewModel(ShellViewModel shell, ILifestreamApiClient? api, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _api = api;
        _clock = clock ?? TimeProvider.System;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>The rows, in group order (Recent, Donors, Patients, Requests, Actions, Go to, Settings).</summary>
    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    /// <summary>True while the server search for the current query runs.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    /// <summary>"No results for "xyz"" when nothing matched; null otherwise.</summary>
    [ObservableProperty]
    public partial string? EmptyText { get; set; }

    /// <summary>Completes when the last server search has finished (tests).</summary>
    public Task PendingSearch { get; private set; } = Task.CompletedTask;

    public void Open()
    {
        Query = string.Empty;
        Rebuild([]);
        IsOpen = true;
    }

    public void Close()
    {
        _search?.Cancel();
        IsOpen = false;
    }

    /// <summary>Moves the selection by <paramref name="delta"/> rows, wrapping at both ends.</summary>
    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }

        SelectedIndex = ((SelectedIndex + delta) % Items.Count + Items.Count) % Items.Count;
    }

    /// <summary>Runs the selected row (Enter).</summary>
    public Task RunSelectedAsync() => SelectedIndex >= 0 && SelectedIndex < Items.Count ? RunAsync(Items[SelectedIndex]) : Task.CompletedTask;

    /// <summary>Closes the palette, remembers the row as recent, then runs it.</summary>
    public async Task RunAsync(PaletteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Close();
        _recent.RemoveAll(r => r.Title == item.Title && r.Subtitle == item.Subtitle);
        _recent.Insert(0, item with { Group = "Recent" });
        if (_recent.Count > MaxRecent)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }

        await item.Run().ConfigureAwait(true);
    }

    partial void OnQueryChanged(string value)
    {
        _search?.Cancel();
        Rebuild([]);
        var query = value.Trim();
        if (_api is null || query.Length < 2)
        {
            IsSearching = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _search = cts;
        PendingSearch = SearchAsync(query, cts.Token);
    }

    private async Task SearchAsync(string query, CancellationToken ct)
    {
        IsSearching = true;
        try
        {
            await Task.Delay(SearchDelay, _clock, ct).ConfigureAwait(true);
            var found = await _shell.Operations.RunAsync(token => FindRecordsAsync(query, token), ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested)
            {
                Rebuild(found);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer query took over.
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or System.Text.Json.JsonException)
        {
            // The palette still offers the screens and actions; the record search is a convenience.
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>Donors, patients and requests matching the query, through the API's record search (at most five of each kind the user can open).</summary>
    [RunsInOperation("SearchAsync runs it inside the shell's Operations.RunAsync.")]
    private async Task<List<PaletteItem>> FindRecordsAsync(string query, CancellationToken ct)
    {
        var kinds = new (RecordKind Kind, string Group, string Screen, string Icon)[]
        {
            (RecordKind.Donor, "Donors", "Donors", "Donor"),
            (RecordKind.Patient, "Patients", "Patients", "Patient"),
            (RecordKind.Request, "Requests", "Requests", "Request"),
        };
        var found = new List<PaletteItem>();
        foreach (var (kind, group, screen, icon) in kinds)
        {
            if (!_shell.Items.Any(i => i.Title == screen))
            {
                continue;
            }

            var result = await _api!.SearchRecordsAsync(kind, query, ct).ConfigureAwait(true);
            if (result.IsOk)
            {
                found.AddRange(result.Value.Take(MaxPerKind).Select(o =>
                    new PaletteItem(group, o.Title, o.Subtitle, icon, null, () => _shell.OpenByTitleAsync(screen, new ListPrefilter(RecordId: o.Id)))));
            }
        }

        return found;
    }

    /// <summary>The static rows: actions, screens and settings.</summary>
    private IEnumerable<PaletteItem> Commands()
    {
        var screens = _shell.Items.Select(i => i.Title).ToHashSet();
        if (screens.Contains("Appointments"))
        {
            yield return new("Actions", "Record donation", "For today's next appointment", "Drop", "Ctrl+D", () => _shell.RecordDonationCommand.ExecuteAsync());
            yield return new("Actions", "Book appointment", null, "Calplus", null, () => _shell.BookAppointmentCommand.ExecuteAsync());
        }

        foreach (var (screen, title, icon) in new[] { ("Donors", "Add donor", "Userplus"), ("Patients", "Add patient", "Userplus"), ("Requests", "New request", "Plus") })
        {
            if (screens.Contains(screen))
            {
                yield return new("Actions", title, null, icon, null, async () =>
                {
                    if (await _shell.OpenByTitleAsync(screen).ConfigureAwait(true) && _shell.Navigation.Current?.NewCommand is { } create)
                    {
                        await create.ExecuteAsync().ConfigureAwait(true);
                    }
                });
            }
        }

        foreach (var item in _shell.Items)
        {
            yield return new("Go to", item.Title, null, item.Icon, item.Shortcut, () => _shell.OpenAsync(item.Destination));
        }

        yield return new("Go to", "Profile", null, "User", null, () => _shell.ProfileCommand.ExecuteAsync());
        yield return new("Settings", "Theme: Light", null, "Sun", null, () => _shell.SetThemeCommand.ExecuteAsync(ThemeMode.Light));
        yield return new("Settings", "Theme: Dark", null, "Moon", null, () => _shell.SetThemeCommand.ExecuteAsync(ThemeMode.Dark));
        yield return new("Settings", "Theme: Match Windows", null, "Monitor", null, () => _shell.SetThemeCommand.ExecuteAsync(ThemeMode.System));
        yield return new("Settings", _shell.IsNavCollapsed ? "Expand the sidebar" : "Collapse the sidebar", null, "Sidebar", "Ctrl+B", () => _shell.ToggleNavCommand.ExecuteAsync());
        yield return new("Settings", "Change password", null, "Key", null, () => _shell.ChangePasswordCommand.ExecuteAsync());
        yield return new("Settings", "Sign out", null, "Logout", null, () => _shell.SignOutCommand.ExecuteAsync());
    }

    /// <summary>Whether every word of the query appears in the row's title or subtitle.</summary>
    public static bool Matches(PaletteItem item, string query)
    {
        ArgumentNullException.ThrowIfNull(item);
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = item.Title + " " + item.Subtitle + " " + item.Group;
        return words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Rebuild(IReadOnlyList<PaletteItem> records)
    {
        var query = Query.Trim();
        var rows = new List<PaletteItem>();
        if (query.Length == 0)
        {
            rows.AddRange(_recent);
            rows.AddRange(Commands().Where(c => !_recent.Any(r => r.Title == c.Title && r.Subtitle == c.Subtitle)));
        }
        else
        {
            rows.AddRange(records);
            rows.AddRange(Commands().Where(c => Matches(c, query)));
        }

        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }

        SelectedIndex = Items.Count > 0 ? 0 : -1;
        EmptyText = Items.Count == 0 && query.Length > 0 && !IsSearching ? $"Nothing matches “{query}”. Try a name, a phone number or a request number." : null;
    }
}
