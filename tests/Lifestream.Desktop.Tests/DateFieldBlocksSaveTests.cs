using System.Windows.Controls;
using System.Windows.Data;
using Lifestream.Client.Validation;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// Real-app walk, defect 14: with "31 Feb 2030" typed, the date field showed an error, yet Save went through and stored the previous date. The
/// date field now tells the form it is bound to, and the form's validation (which every save runs first: <c>RecordEditorViewModel.SaveAsync</c>
/// and <c>DialogViewModel</c>'s primary action both start with <see cref="FormViewModelBase.ValidateAll"/>) refuses to save until the text is a date.
/// </summary>
public sealed class DateFieldBlocksSaveTests
{
    /// <summary>A form with one date and no rule of its own for it, so only the date field's refusal can block the save.</summary>
    private sealed class DonorForm : FormViewModelBase
    {
        public DonorForm() => DeclareFields(nameof(DateOfBirth));

        public DateOnly? DateOfBirth { get; set; } = new DateOnly(1990, 3, 5);

        public override bool HasUnsavedChanges => false;

        public List<string> Focused { get; } = [];

        protected override string? ValidateField(string field) => null;

        /// <summary>What every form's Save does first.</summary>
        public bool TrySave() => ValidateAll();
    }

    [Theory]
    [InlineData("31 Feb 2030", "There is no 31 Feb 2030.")]
    [InlineData("03/05/2022", "Use the format 05 Mar 2022.")]
    public void Save_IsBlocked_WhileTheDateFieldHoldsTextThatIsNotADate_AndGoesThroughOnceItIs(string typed, string message)
    {
        var f = StaHost.Run(() =>
        {
            var form = new DonorForm();
            form.FocusRequested += (_, field) => form.Focused.Add(field);
            var field = new DateField { DataContext = form };
            field.SetBinding(DateField.SelectedDateProperty, new Binding(nameof(DonorForm.DateOfBirth)) { Mode = BindingMode.TwoWay });
            using var host = Hosted.Create(new StackPanel { Children = { field } }, 500, 200);
            host.Settle(100);
            var savedBefore = form.TrySave();

            field.TextBox.Text = typed;
            field.CommitText(); // as on blur or Enter
            var saved = form.TrySave();
            var shown = (form.GetError(nameof(DonorForm.DateOfBirth)), form.ErrorSummary, string.Join(",", form.Focused), form.DateOfBirth);

            field.TextBox.Text = "02 Jan 1991";
            field.CommitText();
            return (savedBefore, saved, shown, SavedAfter: form.TrySave(), After: (form.GetError(nameof(DonorForm.DateOfBirth)), form.DateOfBirth, field.ParseError));
        });

        Assert.True(f.savedBefore);
        Assert.False(f.saved, "Save went through while the date field showed an error"); // released 2.1.0 and 2.1.1: saved, with the previous date
        Assert.Equal((message, "1 field needs attention.", "DateOfBirth", new DateOnly(1990, 3, 5)), f.shown);
        Assert.True(f.SavedAfter);
        Assert.Equal((null, new DateOnly(1991, 1, 2), null), f.After);
    }

    /// <summary>
    /// A save that does not take the focus out of the date box (Ctrl+S, Enter on the default button of a dialog, a screen reader's Invoke on
    /// Save) never ran the box's blur or Enter handling in 2.1.2, so the form saved the date from before, whatever was typed.
    /// </summary>
    [Fact]
    public void Save_WithTheCaretStillInTheDateBox_SavesTheDateThatWasTyped_OrIsRefused_NeverTheDateFromBefore()
    {
        var f = StaHost.Run(() =>
        {
            var form = new DonorForm();
            var field = new DateField { DataContext = form };
            field.SetBinding(DateField.SelectedDateProperty, new Binding(nameof(DonorForm.DateOfBirth)) { Mode = BindingMode.TwoWay });
            using var host = Hosted.Create(new StackPanel { Children = { field } }, 500, 200);
            host.Settle(100);

            // Typed, and nothing else: no Enter, no blur.
            field.TextBox.Text = "02 Jan 1991";
            var typedDate = (form.TrySave(), form.DateOfBirth, field.TextBox.Text);

            field.TextBox.Text = "31 Feb 2030";
            var whileTyping = (form.GetError(nameof(DonorForm.DateOfBirth)), field.ParseError); // no message while the person is still typing
            var impossible = (form.TrySave(), form.GetError(nameof(DonorForm.DateOfBirth)), form.DateOfBirth);

            field.TextBox.Text = "02 Jan 199";
            var halfTyped = (form.TrySave(), form.GetError(nameof(DonorForm.DateOfBirth)));

            field.TextBox.Text = "03 Jan 1991";
            var corrected = (form.TrySave(), form.GetError(nameof(DonorForm.DateOfBirth)), form.DateOfBirth);

            field.TextBox.Text = string.Empty;
            var cleared = (form.TrySave(), form.DateOfBirth);
            return (typedDate, whileTyping, impossible, halfTyped, corrected, cleared);
        });

        Assert.Equal((true, new DateOnly(1991, 1, 2), "02 Jan 1991"), f.typedDate); // released 2.1.2: saved 05 Mar 1990, the date from before
        Assert.Equal((null, null), f.whileTyping);
        Assert.Equal((false, "There is no 31 Feb 2030.", new DateOnly(1991, 1, 2)), f.impossible); // released 2.1.2: saved, with the date from before
        Assert.Equal((false, "Use the format 05 Mar 2022."), f.halfTyped);
        Assert.Equal((true, null, new DateOnly(1991, 1, 3)), f.corrected);
        Assert.Equal((true, null), f.cleared);
    }
}
