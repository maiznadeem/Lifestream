using System.Windows.Controls;

namespace Lifestream.Desktop;

public partial class SpikeView : UserControl
{
    public SpikeView()
    {
        InitializeComponent();
        DataContext = new SpikeViewModel();
    }
}
