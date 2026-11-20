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
    [InlineData("31 Feb 2030", "31 Feb 2030 is not a date. Check the day and the month.")]
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
}
