using Lifestream.Client.Features.ListScreens;

namespace Lifestream.Client.Tests.Features;

/// <summary>SHELL-24: starting a new record (Add button, Ctrl+N) while the open editor has unsaved changes asks "Discard changes?" (fake API).</summary>
public sealed class NewWhileDirtyTests
{
    private static async Task<(FeatureRig Rig, Lifestream.Client.Features.Donors.DonorsViewModel Vm, PersonEditorViewModel First)> DirtyEditor()
    {
        var rig = new FeatureRig();
        var vm = rig.Donors();
        await vm.AddCommand.ExecuteAsync();
        var first = (PersonEditorViewModel)vm.Editor!;
        first.Name = "Maria Lopez";
        Assert.True(vm.HasUnsavedChanges);
        return (rig, vm, first);
    }

    [Fact]
    public async Task AddWhileEditorDirty_Declined_KeepsTheTypedEditor_FakeApi()
    {
        var (rig, vm, first) = await DirtyEditor();
        rig.Dialogs.Answer = false;

        await vm.AddCommand.ExecuteAsync();

        Assert.Equal("Discard changes?", Assert.Single(rig.Dialogs.Confirmations).Title);
        Assert.Same(first, vm.Editor);
        Assert.Equal("Maria Lopez", first.Name);
    }

    [Fact]
    public async Task CtrlNWhileEditorDirty_Accepted_OpensABlankEditor_FakeApi()
    {
        var (rig, vm, first) = await DirtyEditor();
        rig.Dialogs.Answer = true;

        await vm.NewCommand!.ExecuteAsync();

        Assert.Equal("Discard changes?", Assert.Single(rig.Dialogs.Confirmations).Title);
        Assert.NotSame(first, vm.Editor);
        Assert.Equal(string.Empty, ((PersonEditorViewModel)vm.Editor!).Name);
    }

    [Fact]
    public async Task AddWhileEditorClean_DoesNotAsk_FakeApi()
    {
        var rig = new FeatureRig();
        var vm = rig.Donors();
        await vm.AddCommand.ExecuteAsync();
        var first = vm.Editor;

        await vm.AddCommand.ExecuteAsync();

        Assert.Empty(rig.Dialogs.Confirmations);
        Assert.NotNull(vm.Editor);
        Assert.NotSame(first, vm.Editor);
    }
}
