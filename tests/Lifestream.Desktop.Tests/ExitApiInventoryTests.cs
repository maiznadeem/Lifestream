using System.Reflection;
using System.Reflection.Emit;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// SHELL-24, inventory of the exit APIs (fourth round). Reading the IL of the client and desktop assemblies, this finds every method that can end the
/// process, close a window, apply an update (which restarts the app), replace the current screen or sign out, and requires each one to be on a reviewed
/// list with the reason it is safe when a form has unsaved changes: either the path is guarded and a named test runs it with a dirty form (the shell
/// commands: ExitPathsGuardTests; closing the main window: MainWindowCloseTests; a dialog's Cancel/Esc/X: UnsavedChangesInventoryTests), or no form with
/// unsaved changes can exist at that point. A new call to an exit API fails here until it is reviewed.
/// </summary>
public sealed class ExitApiInventoryTests
{
    private static readonly Dictionary<short, OpCode> OpCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>The exit and screen-replacing APIs: declaring type full name + method name.</summary>
    private static readonly HashSet<string> ExitApis =
    [
        "System.Windows.Application.Shutdown",
        "System.Windows.Window.Close",
        "Velopack.UpdateManager.ApplyUpdatesAndRestart",
        "Velopack.UpdateManager.ApplyUpdatesAndExit",
        "Velopack.UpdateManager.WaitExitThenApplyUpdates",
        "System.Environment.Exit",
        "System.Environment.FailFast",
        "System.Diagnostics.Process.Kill",
        "Lifestream.Client.Navigation.INavigationService.NavigateTo",
        "Lifestream.Client.Navigation.INavigationService.Reset",
        "Lifestream.Client.Navigation.IWindowHost.CloseMain",
        "Lifestream.Client.Navigation.IWindowHost.CloseLogin",
        "Lifestream.Client.Navigation.IWindowHost.ShowMain",
        "Lifestream.Client.Navigation.IWindowHost.ShowLogin",
        "Lifestream.Client.Navigation.IAppFlow.SignOutAsync",
        "Lifestream.Client.Updates.IUpdateSource.ApplyAsync",
        "Lifestream.Client.Updates.UpdateService.ApplyAsync",
    ];

    private const string Guarded = "guarded: ";
    private const string NoForm = "no form can have unsaved changes here: ";

    /// <summary>Every method that calls an exit API (type.method as the author wrote it, lambdas and async bodies folded into their method), with why it is safe.</summary>
    private static readonly Dictionary<string, string> Reviewed = new()
    {
        // Desktop: the window host and the update feed.
        ["WpfWindowHost.ShowLogin"] = NoForm + "the login window closing ends the app only while no main window exists, so there is no signed-in screen",
        ["WpfWindowHost.CloseLogin"] = NoForm + "closes the sign-in window after a successful sign-in",
        ["WpfWindowHost.ShowMain"] = Guarded + "when the main window closes the process ends; the user's close goes through MainWindow.OnClosing -> ConfirmCloseAsync (MainWindowCloseTests, DiscardChangesTests.ClosingTheMainWindow)",
        ["VelopackFeed.ApplyAsync"] = Guarded + "reached only through UpdateService.ApplyAsync from ShellViewModel.ApplyUpdateCommand, which asks first (ExitPathsGuardTests.RestartAndUpdate...), and from the sign-in window's banner (no form there)",
        ["WpfFormDialogPresenter.Show"] = Guarded + "a dialog window closes only when its view model asks it to, after the dialog's own Discard changes? (UnsavedChangesInventoryTests, DialogAndFieldViewTests)",
        ["LaunchProbe.OnFirstRender"] = NoForm + "the launch measurement mode (an environment switch for the perf harness) ends the app after the first frame of the sign-in window",
        // Client: the flows and the commands.
        ["AppFlow.SignOutAsync"] = Guarded + "asks navigation.ConfirmDiscardAsync unless forced by an expired session (DiscardChangesTests)",
        ["AppFlow.SignedInAsync"] = NoForm + "runs right after sign-in: replaces the sign-in window",
        ["AppFlow.Start"] = NoForm + "shows the sign-in window",
        ["UpdateService.ApplyAsync"] = Guarded + "called by ShellViewModel.ApplyUpdateCommand after the prompt, and by LoginViewModel.ApplyUpdateCommand (the sign-in window has no form)",
        ["ShellViewModel..ctor"] = Guarded + "the lambdas of the shell's commands (sidebar, profile, sign out, apply update, back to dashboard): ExitPathsGuardTests runs every one of them with a dirty form and a user who stays",
        ["ShellViewModel.StartAsync"] = NoForm + "a forced sign-out when the user declined the required password change, before any screen is open",
        ["ShellViewModel.Dispose"] = NoForm + "runs when the main window is already closing",
        ["LoginViewModel..ctor"] = NoForm + "the sign-in window's update banner: the sign-in window has nothing to save",
        ["SessionExpiredWatcher.Start"] = NoForm + "the session already expired: the forced sign-out cannot keep the user signed in, and says so (DiscardChangesTests.SignOut_ByAnExpiredSession)",
        ["MainWindow.CloseWithoutPrompt"] = Guarded + "called after ConfirmCloseAsync agreed (MainWindow.ConfirmThenCloseAsync) or by the host's CloseMain after a guarded sign-out (MainWindowCloseTests, DiscardChangesTests)",
    };

    private static string Origin(Type type, MethodBase method)
    {
        var outer = type;
        while (outer.DeclaringType is { } up)
        {
            outer = up;
        }

        static string? Between(string name) => name.StartsWith('<') && name.IndexOf('>', StringComparison.Ordinal) is var end and > 0 ? name[1..end] : null;
        var origin = Between(method.Name) ?? Between(type.Name) ?? method.Name;
        origin = origin.TrimStart('<');
        if (origin is "-ctor" or ".ctor" or "")
        {
            origin = ".ctor"; // lambdas made in a constructor (the commands)
        }

        return $"{outer.Name}.{origin}";
    }

    private static IEnumerable<(string Caller, string Api)> Calls(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
                {
                    continue;
                }

                var module = method.Module;
                Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
                Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                for (var i = 0; i < il.Length;)
                {
                    var code = il[i] == 0xFE ? OpCodes[(short)(0xFE00 | il[i + 1])] : OpCodes[il[i]];
                    i += code.Size;
                    if (code.OperandType == OperandType.InlineMethod)
                    {
                        MethodBase? target = null;
                        try
                        {
                            target = module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs);
                        }
                        catch (ArgumentException)
                        {
                            // Not resolvable here: not one of the APIs.
                        }

                        var api = target?.DeclaringType is { } owner ? $"{owner.FullName}.{target!.Name}" : null;
                        if (api is not null && ExitApis.Contains(api))
                        {
                            yield return (Origin(type, method), api);
                        }
                    }

                    i += code.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                        _ => 4,
                    };
                }
            }
        }
    }

    [Fact]
    public void EveryMethodThatCanExitCloseOrReplaceTheScreen_IsReviewed_WithTheTestThatGuardsIt()
    {
        var sites = Calls(typeof(Lifestream.Desktop.App).Assembly).Concat(Calls(typeof(Lifestream.Client.ClientServices).Assembly)).Distinct().ToList();
        var unreviewed = sites
            .Where(s => !Reviewed.ContainsKey(s.Caller) && !(s.Api.EndsWith("INavigationService.NavigateTo", StringComparison.Ordinal) && IsScreenCommand(s.Caller)))
            .Select(s => $"{s.Caller} calls {s.Api}")
            .Distinct()
            .Order()
            .ToList();
        var stale = Reviewed.Keys.Where(k => !sites.Any(s => s.Caller == k)).Order().ToList();

        Assert.True(sites.Count >= 15, $"only {sites.Count} exit call sites were found: the scan is not reading the assemblies");
        Assert.True(unreviewed.Count == 0, "These methods can exit, close a window or replace the screen and are not on the reviewed list (add them with the test that proves the guard, or the reason no form can be dirty):" + Environment.NewLine + string.Join(Environment.NewLine, unreviewed));
        Assert.True(stale.Count == 0, "Reviewed entries that no longer call an exit API (remove them): " + string.Join(", ", stale));
        Assert.All(Reviewed.Values, reason => Assert.True(reason.StartsWith(Guarded, StringComparison.Ordinal) || reason.StartsWith(NoForm, StringComparison.Ordinal)));
    }

    /// <summary>A view model's own navigation (open the donor, book an appointment, an action on the dashboard): it navigates through the guarded service.</summary>
    private static bool IsScreenCommand(string caller) => caller.Split('.')[0].EndsWith("ViewModel", StringComparison.Ordinal);
}
