using System.Xml.Linq;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// The app runs without dynamic PGO (src/Lifestream.Desktop/Lifestream.Desktop.csproj: with it, the first visits of every screen ran instrumented code and
/// cost about twice the later ones), and this test process runs with the same setting, so what the view tests and the perf pass measure is what the app does.
/// </summary>
public sealed class RuntimeSettingsTests
{
    [Fact]
    public void TheApp_AndTheProcessThatMeasuresItsViews_RunWithoutDynamicPgo()
    {
        var project = XDocument.Load(Path.Combine(Lifestream.Client.Tests.RepoPaths.Find("src/Lifestream.Desktop"), "Lifestream.Desktop.csproj"));
        var declared = project.Descendants("TieredPGO").Select(e => e.Value.Trim()).ToList();

        Assert.Equal(["false"], declared);
        Assert.Equal("false", Convert.ToString(AppContext.GetData("System.Runtime.TieredPGO"), System.Globalization.CultureInfo.InvariantCulture)?.ToLowerInvariant());
    }
}
