using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Staff;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Features.Staff;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// Real-app walk 15: "Use 5 to 20 characters: digit" ended mid-word under the phone box. The form field's error row was a horizontal stack, so
/// the message was laid out on one line however narrow the field. A field's hint and error take the field's width and wrap, in every form.
/// </summary>
[Collection("Wpf")] // the list screens set the record picker's process-wide test hooks: one such class at a time
public sealed class FormErrorWrapTests
{
    private const string Sentence = "Use 5 to 20 characters: digits, spaces and the signs + - ( ).";

    [Fact]
    public void FormField_InANarrowColumn_WrapsItsErrorAndItsHint_InsideTheField()
    {
        var f = StaHost.Run(() =>
        {
            var field = new FormField { Label = "Phone (optional)", Hint = "Include the country code, for example +1 555 0175", Content = new TextBox(), Width = 178 };
            using var host = Hosted.Create(new StackPanel { Children = { field }, HorizontalAlignment = HorizontalAlignment.Left }, 400, 300);
            host.Settle(100);
            var hint = (TextBlock)field.Template.FindName("HintText", field);
            var hintFacts = (hint.ActualWidth, Lines: hint.ActualHeight / hint.LineHeight);

            field.Error = Sentence;
            host.Settle(100);
            host.Capture("fix212b-formfield-error-wraps.png");
            var error = (TextBlock)field.Template.FindName("PART_Error", field);
            var icon = Hosted.Descendants(field).OfType<Icon>().Single(i => i.IsVisible);
            return (hintFacts, ErrorRight: Hosted.BoundsOf(error, field).Right, ErrorLines: error.ActualHeight / error.LineHeight, error.Text,
                IconOnFirstLine: Hosted.BoundsOf(icon, field).Top - Hosted.BoundsOf(error, field).Top);
        });

        Assert.True(f.hintFacts.ActualWidth <= 178.5 && f.hintFacts.Lines >= 2, $"the hint does not wrap: {f.hintFacts}");
        Assert.Equal(Sentence, f.Text);
        Assert.True(f.ErrorRight <= 178.5, $"the error runs past the field, to {f.ErrorRight}"); // released 2.1.2: far past it, and cut off there
        Assert.True(f.ErrorLines >= 2, "the error is on one line");
        Assert.InRange(f.IconOnFirstLine, -1, 3);
    }

    public enum Screen
    {
        Donors,
        Patients,
        Staff,
        Appointments,
        Requests,
    }

    [Theory]
    [InlineData(Screen.Donors)]
    [InlineData(Screen.Patients)]
    [InlineData(Screen.Staff)]
    [InlineData(Screen.Appointments)]
    [InlineData(Screen.Requests)]
    public void EveryForm_AtTheCompactPanelWidth_ShowsALongErrorWhole_UnderEachOfItsFields(Screen screen)
    {
        var f = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            (ViewModelBase vm, UserControl view) = screen switch
            {
                Screen.Donors => ((ViewModelBase)Part2Harness.Create<DonorsViewModel>(rig), (UserControl)new DonorsView()),
                Screen.Patients => (Part2Harness.Create<PatientsViewModel>(rig), new PatientsView()),
                Screen.Staff => (Part2Harness.Create<StaffViewModel>(rig), new StaffView()),
                Screen.Appointments => (Part2Harness.Create<AppointmentsViewModel>(rig), new AppointmentsView()),
                _ => (Part2Harness.Create<RequestsViewModel>(rig), new RequestsView()),
            };
            using var host = Hosted.Create(Part2Harness.OnCanvas(view, vm), 1024, 700); // the minimum window: the compact panel
            Hosted.Await(() => vm.LoadAsync());
            Hosted.Await(() => vm.NewCommand!.ExecuteAsync());
            host.Settle(500);

            var fields = host.All<FormField>().Where(x => x.IsVisible).ToList();
            foreach (var field in fields)
            {
                field.Error = Sentence;
            }

            host.Settle(300);
            Part2Harness.Save(host, $"fix212b-form-errors-{screen}.png");
            var problems = new List<string>();
            foreach (var field in fields)
            {
                var error = (TextBlock)field.Template.FindName("PART_Error", field);
                var right = Hosted.BoundsOf(error, field).Right;
                if (!error.IsVisible || right > field.ActualWidth + 0.5)
                {
                    problems.Add($"{field.Label}: the message ends at {right:0} in a field {field.ActualWidth:0} wide");
                }
            }

            return (fields.Count, problems);
        });

        Assert.True(f.Count >= 2, $"the {screen} form did not open ({f.Count} fields)");
        Assert.Empty(f.problems);
    }
}
