using Lifestream.Client.Common;
using Lifestream.Desktop.Services;

namespace Lifestream.Desktop.Tests;

/// <summary>The UI thread of a test, for services that must change bound state there (SHELL-01). Call it on the STA thread that hosts the views.</summary>
public static class WpfTestUi
{
    public static IUiDispatcher Dispatcher() => new WpfUiDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
}
