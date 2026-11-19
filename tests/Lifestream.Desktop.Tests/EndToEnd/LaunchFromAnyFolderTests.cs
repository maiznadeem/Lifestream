using System.Diagnostics;
using System.IO.Pipes;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// The app finds its appsettings.json beside the exe, whatever folder it is started from (a shortcut may start it anywhere). Starts the
/// published exe (set LIFESTREAM_PUBLISHED_EXE; run-perf.ps1 does) with the temp folder as working directory and the off-desktop
/// <c>--perf-launch-pipe</c> switch, and asserts it reaches the login window. Category=Perf only so the default run does not need a publish.
/// </summary>
[Trait("Category", "Perf")]
[Trait("PerfPhase", "launch-cwd")]
public sealed class LaunchFromAnyFolderTests
{
    [Fact]
    public async Task PublishedApp_StartedFromTheTempFolder_ReachesTheLoginWindow()
    {
        var exe = Environment.GetEnvironmentVariable("LIFESTREAM_PUBLISHED_EXE") is { Length: > 0 } e ? e : throw new InvalidOperationException("LIFESTREAM_PUBLISHED_EXE is not set (run-perf.ps1 sets it).");
        var pipeName = "lifestream-cwd-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var temp = Path.GetTempPath();
        var psi = new ProcessStartInfo(exe, $"--perf-launch-pipe {pipeName}") { UseShellExecute = false, WorkingDirectory = temp };
        using var process = Process.Start(psi)!;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await server.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(server);
            Assert.StartsWith("rendered ", await reader.ReadLineAsync(timeout.Token), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.WaitForExit(10_000))
            {
                process.Kill(true);
            }
        }
    }
}
