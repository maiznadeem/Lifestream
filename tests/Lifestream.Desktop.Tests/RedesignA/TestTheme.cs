using System.Runtime.CompilerServices;
using Lifestream.Desktop.Theme;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>Tests run in the light theme whatever the machine's Windows mode is, unless a test picks a theme itself (ThemeService.Apply).</summary>
internal static class TestTheme
{
    [ModuleInitializer]
    internal static void UseLightByDefault() => ThemeService.SystemIsDarkOverride = false;
}
