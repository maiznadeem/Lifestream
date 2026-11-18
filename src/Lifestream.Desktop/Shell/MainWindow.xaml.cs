using Lifestream.Desktop.Chrome;

namespace Lifestream.Desktop.Shell;

/// <summary>The main window (minimum 1024 x 640 DIP). The window chrome is the shared <see cref="ChromeWindow"/>.</summary>
public partial class MainWindow : ChromeWindow
{
    public MainWindow() => InitializeComponent();
}
