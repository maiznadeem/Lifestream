using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Lifestream.Client.Updates;

/// <summary>Where the newest published version comes from (SHELL-44). The Desktop project backs it with the GitHub release feed.</summary>
public interface IUpdateSource
{
    /// <summary>The newest version on the release feed, or null when there is none or the app is not an installed build.</summary>
    Task<Version?> GetLatestVersionAsync(CancellationToken ct);

    /// <summary>Downloads the update and restarts the app into it.</summary>
    Task ApplyAsync(CancellationToken ct);
}

/// <summary>Checks the feed once at startup and remembers whether a newer version than the running one exists.</summary>
public sealed partial class UpdateService(IUpdateSource source, Version currentVersion, ILogger<UpdateService> logger) : ObservableObject
{
    /// <summary>The newer version, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateReady), nameof(Message))]
    public partial Version? AvailableVersion { get; private set; }

    /// <summary>True while the feed is being checked.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>True while an update is being downloaded and applied.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public bool IsUpdateReady => AvailableVersion is not null;

    public string Message => AvailableVersion is null ? string.Empty : $"Version {AvailableVersion.ToString(3)} is ready. Restart to update.";

    public async Task CheckAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var latest = await source.GetLatestVersionAsync(ct).ConfigureAwait(true);
            AvailableVersion = latest is not null && latest > currentVersion ? latest : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreachable feed must never get in the way of using the app.
            LogCheckFailed(logger, ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await source.ApplyAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
