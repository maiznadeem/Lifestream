using Wpf.Ui.Controls;

namespace Lifestream.Desktop;

/// <summary>Stack spike: a WPF-UI FluentWindow hosting a LiveCharts2 chart bound through CommunityToolkit.Mvvm.</summary>
public sealed class SpikeWindow : FluentWindow
{
    public SpikeWindow()
    {
        Title = "Lifestream spike";
        Width = 800;
        Height = 600;
        Content = new SpikeView();
    }
}
