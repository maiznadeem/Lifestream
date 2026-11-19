using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Lifestream.Desktop.Tests;

/// <summary>SHELL-35: every control in the combo box template that UI Automation can see has a name, not only the combo box itself.</summary>
[Collection("Wpf")]
public sealed class ComboBoxAutomationTests
{
    [Theory]
    [InlineData("Blood type")]
    [InlineData(null)]
    public void ComboBox_InnerToggleButton_HasAnAutomationName(string? comboName)
    {
        var names = StaHost.Run(() =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "A", "B" } };
            if (comboName is not null)
            {
                AutomationProperties.SetName(combo, comboName);
            }

            using var host = Hosted.Create(Hosted.Stage(combo), 400, 120);
            host.Settle(150);
            return host.All<ToggleButton>()
                .Where(t => ReferenceEquals(t.TemplatedParent, combo))
                .Select(t => (Name: UIElementAutomationPeer.CreatePeerForElement(t)?.GetName(), Combo: UIElementAutomationPeer.CreatePeerForElement(combo)!.GetName()))
                .ToList();
        });

        var toggle = Assert.Single(names);
        Assert.False(string.IsNullOrWhiteSpace(toggle.Name), "the combo box's toggle button has no automation name");
        if (comboName is not null)
        {
            Assert.Contains(comboName, toggle.Name, StringComparison.Ordinal);
        }
    }
}
