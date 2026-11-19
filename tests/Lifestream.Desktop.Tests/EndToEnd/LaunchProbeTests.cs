using System.IO.Pipes;
using Lifestream.Desktop.Auth;
using Lifestream.Desktop.Services;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>The launch measurement switch (the launch budget of design 5.5): parsed from the command line, keeps the sign-in window off every desktop and never active, and reports over a named pipe.</summary>
[Collection("Wpf")]
public sealed class LaunchProbeTests
{
    [Theory]
    [InlineData("--perf-launch-pipe", "p1", "p1")]
    [InlineData("--other", "x", null)]
    public void ParsePipeName_ReadsTheValueAfterTheSwitch(string first, string second, string? expected) =>
        Assert.Equal(expected, LaunchProbe.ParsePipeName([first, second]));

    [Fact]
    public void ParsePipeName_IsNullWithoutTheSwitch_OrWithoutAValue()
    {
        Assert.Null(LaunchProbe.ParsePipeName([]));
        Assert.Null(LaunchProbe.ParsePipeName(["--perf-launch-pipe"]));
        Assert.Null(LaunchProbe.ParsePipeName(["--perf-launch-pipe", ""]));
    }

    [Fact]
    public void Prepare_PutsTheWindowFarOffTheDesktop_NotActivated_NotInTheTaskBar()
    {
        var (left, top, activated, taskbar, topmost) = StaHost.Run(() =>
        {
            var window = new LoginWindow();
            LaunchProbe.Prepare(window);
            return (window.Left, window.Top, window.ShowActivated, window.ShowInTaskbar, window.Topmost);
        });

        Assert.Equal(-20000, left);
        Assert.Equal(-20000, top);
        Assert.False(activated);
        Assert.False(taskbar);
        Assert.False(topmost);
        Assert.True(left < System.Windows.SystemParameters.VirtualScreenLeft - 10_000);
    }

    [Fact]
    public async Task Report_WritesTheElapsedTimeToTheNamedPipe()
    {
        var name = "lifestream-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var read = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(server);
            return await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        Assert.True(LaunchProbe.Report(name, TimeSpan.FromMilliseconds(123.45)));
        Assert.Equal("rendered 123.5", await read);
    }
}
