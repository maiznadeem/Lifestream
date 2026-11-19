using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Validation;

namespace Lifestream.Client.Tests;

/// <summary>
/// SHELL-24 inventory: one dirty-tracking contract (<see cref="IHasUnsavedChanges"/>) for every editor and every form dialog with inputs, and every
/// way out of them asks "Discard changes?" while dirty. The tests walk the client assembly by reflection, so a new editor or dialog that does not
/// implement the contract (or is not in the catalog) fails here.
/// </summary>
public sealed class UnsavedChangesInventoryTests : IDisposable
{
    private readonly ViewModelCatalog _catalog = new();

    public UnsavedChangesInventoryTests() => _ = _catalog.Factories; // builds the list of forms

    public void Dispose() => _catalog.Dispose();

    private static IEnumerable<Type> FormTypes() =>
        typeof(FormViewModelBase).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(FormViewModelBase).IsAssignableFrom(t));

    [Fact]
    public void EveryFormViewModel_ImplementsTheContract_AndIsInTheDirtyCatalog_OrSaysItHasNoInputs()
    {
        var inCatalog = _catalog.Forms.Select(f => f.Type).ToHashSet();
        var problems = new List<string>();
        foreach (var type in FormTypes())
        {
            if (!typeof(IHasUnsavedChanges).IsAssignableFrom(type))
            {
                problems.Add($"{type.Name} does not implement IHasUnsavedChanges");
            }

            var exemption = (NoUserInputAttribute?)Attribute.GetCustomAttribute(type, typeof(NoUserInputAttribute));
            if (exemption is null && !inCatalog.Contains(type))
            {
                problems.Add($"{type.Name} is not in the catalog of dirty forms (add it to ViewModelCatalog, or mark it [NoUserInput(reason)] if it has no inputs)");
            }

            if (exemption is not null && (exemption.Reason.Length < 15 || inCatalog.Contains(type)))
            {
                problems.Add($"{type.Name}: a [NoUserInput] form needs a real reason and must not be in the dirty catalog");
            }
        }

        Assert.True(_catalog.Forms.Count >= 9, "the dirty catalog should list every editor and every dialog with inputs");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryFormWithInputs_IsCleanWhenOpened_AndDirtyOnceTheUserTypes()
    {
        foreach (var (type, create, makeDirty) in _catalog.Forms)
        {
            var form = create();
            Assert.False(form.HasUnsavedChanges, $"{type.Name} starts dirty");

            makeDirty(form);

            Assert.True(form.HasUnsavedChanges, $"{type.Name} does not notice that the user typed");
        }
    }

    [Fact]
    public async Task EveryEditor_Cancel_AsksDiscardChanges_WhenDirty_AndOnlyThen()
    {
        var checkedEditors = 0;
        foreach (var (type, create, makeDirty) in _catalog.Forms.Where(f => typeof(RecordEditorViewModel).IsAssignableFrom(f.Type)))
        {
            _catalog.Rig.Dialogs.Confirmations.Clear();
            var closed = 0;
            var clean = (RecordEditorViewModel)create();
            clean.Closed += (_, _) => closed++;
            await clean.CancelCommand.ExecuteAsync();
            Assert.True(closed == 1 && _catalog.Rig.Dialogs.Confirmations.Count == 0, $"{type.Name}: a clean editor closes without asking");

            closed = 0;
            var dirty = (RecordEditorViewModel)create();
            dirty.Closed += (_, _) => closed++;
            makeDirty(dirty);
            _catalog.Rig.Dialogs.Answer = false;
            await dirty.CancelCommand.ExecuteAsync();
            Assert.True(closed == 0 && _catalog.Rig.Dialogs.Confirmations is [{ Title: "Discard changes?" }], $"{type.Name}: a dirty editor must ask 'Discard changes?' and stay open when refused");

            _catalog.Rig.Dialogs.Answer = true;
            await dirty.CancelCommand.ExecuteAsync();
            Assert.True(closed == 1, $"{type.Name}: closes when the user agrees to discard");
            checkedEditors++;
        }

        Assert.True(checkedEditors >= 5, $"only {checkedEditors} editors were checked");
    }

    [Fact]
    public async Task EveryFormDialog_EscCancelAndX_AskDiscardChanges_WhenDirty_AndOnlyThen()
    {
        var checkedDialogs = 0;
        foreach (var (type, create, makeDirty) in _catalog.Forms.Where(f => typeof(DialogViewModel).IsAssignableFrom(f.Type)))
        {
            var dialogs = new FakeDialogs { Answer = false };

            var clean = (DialogViewModel)create();
            clean.Bind(null, TimeProvider.System, dialogs);
            var closedClean = 0;
            clean.CloseRequested += (_, _) => closedClean++;
            await clean.CancelCommand.ExecuteAsync(); // what Esc, the Cancel button and the window's X all run
            Assert.True(closedClean == 1 && dialogs.Confirmations.Count == 0, $"{type.Name}: a clean dialog closes without asking");

            var dirty = (DialogViewModel)create();
            dirty.Bind(null, TimeProvider.System, dialogs);
            var closed = 0;
            dirty.CloseRequested += (_, _) => closed++;
            makeDirty(dirty);

            await dirty.CancelCommand.ExecuteAsync();
            Assert.True(closed == 0 && !dirty.IsClosing && dialogs.Confirmations is [{ Title: "Discard changes?", Verb: "Discard changes" }], $"{type.Name}: a dirty dialog must ask 'Discard changes?' and stay open when the user refuses");

            dialogs.Answer = true;
            await dirty.CancelCommand.ExecuteAsync();
            Assert.True(closed == 1 && dirty.IsClosing, $"{type.Name}: closes when the user agrees to discard");
            checkedDialogs++;
        }

        Assert.True(checkedDialogs >= 3, $"only {checkedDialogs} dialogs were checked");
    }

    [Fact]
    public async Task ADirtyDialog_ThatWasNotOpenedThroughTheFormDialogService_RefusesToCloseSilently()
    {
        var (_, create, makeDirty) = _catalog.Forms.First(f => f.Type == typeof(Lifestream.Client.Features.Profile.ChangePasswordDialogViewModel));
        var dialog = (DialogViewModel)create();
        var closed = 0;
        dialog.CloseRequested += (_, _) => closed++;
        makeDirty(dialog);
        var reported = new List<Exception>();
        using (AsyncRelayCommand.CaptureErrors(reported.Add))
        {
            await dialog.CancelCommand.ExecuteAsync();
        }

        Assert.Equal(0, closed); // cannot ask, so it does not discard what was typed
        Assert.IsType<InvalidOperationException>(dialog.CancelCommand.LastException);
        Assert.Same(dialog.CancelCommand.LastException, Assert.Single(reported));
    }

    [Fact]
    public async Task FormDialogService_GivesEveryDialogItsDialogService()
    {
        var dialogs = new FakeDialogs { Answer = false };
        var service = new FormDialogService(null, dialogs) { Presenter = new ClosingPresenter() };
        var (_, create, makeDirty) = _catalog.Forms.First(f => f.Type == typeof(Lifestream.Client.Features.Staff.ResetPasswordDialogViewModel));
        var dialog = (DialogViewModel)create();
        makeDirty(dialog);

        await service.ShowAsync(dialog);

        Assert.Equal(["Discard changes?"], dialogs.Confirmations.Select(c => c.Title));
    }

    /// <summary>A presenter that acts as a user who presses Esc once.</summary>
    private sealed class ClosingPresenter : IFormDialogPresenter
    {
        public Task ShowAsync(DialogViewModel viewModel) => viewModel.CancelCommand.ExecuteAsync();
    }
}
