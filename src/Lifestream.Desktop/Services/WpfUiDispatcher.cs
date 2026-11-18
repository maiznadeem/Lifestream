using System.Windows.Threading;
using Lifestream.Client.Common;

namespace Lifestream.Desktop.Services;

/// <summary>The WPF dispatcher as the client's <see cref="IUiDispatcher"/> (SHELL-01). It is given the dispatcher; it never looks one up.</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        dispatcher.BeginInvoke(action);
    }
}
