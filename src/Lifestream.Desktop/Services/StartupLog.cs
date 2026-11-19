using Microsoft.Extensions.Logging;

namespace Lifestream.Desktop.Services;

/// <summary>Log messages of the startup sequence (a font that could not be loaded is a warning, not a crash, SHELL-01).</summary>
internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    public static partial void Warning(ILogger logger, string message);
}
