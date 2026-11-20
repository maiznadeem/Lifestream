using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Lifestream.Desktop.Tests;

/// <summary>
/// A test window never becomes the active or the foreground window, even when a test gives an element in it the keyboard focus: the way the
/// hidden "LifestreamHosted" window took the person's foreground and keyboard on 2026-11-20/21 (<see cref="TestWindowGuard"/>). It holds on
/// any desktop: the window must not even be its thread's active window, which it became on the isolated desktop before the guard.
/// </summary>
public sealed class TestWindowGuardTests
{
    [Fact]
    public void FocusingAnElementOfAHostedTestWindow_NeverActivatesTheWindow_NorMakesItTheForegroundWindow()
    {
        var facts = StaHost.Run(() =>
        {
            var box = new TextBox { Width = 200 };
            using var hosted = Hosted.Create(new StackPanel { Children = { box } }, 300, 100);
            hosted.Settle();
            box.Focus();
            Keyboard.Focus(box);
            hosted.Settle();
            var hwnd = ((HwndSource)PresentationSource.FromVisual(box)).Handle;
            return (Hwnd: hwnd, Active: GetActiveWindow(), Focus: GetFocus(), Foreground: GetForegroundWindow(), WpfFocus: box.IsKeyboardFocused);
        });

        Assert.Equal(IntPtr.Zero, facts.Active);
        Assert.NotEqual(facts.Hwnd, facts.Focus);
        Assert.NotEqual(facts.Hwnd, facts.Foreground);
    }

    [Fact]
    public void ARenderedElement_NeverActivatesItsWindow()
    {
        var active = StaHost.Run(() =>
        {
            var box = new TextBox { Width = 200 };
            box.Loaded += (_, _) => Keyboard.Focus(box);
            _ = StaHost.RenderPng(box, 300, 60, settleMilliseconds: 300);
            return GetActiveWindow();
        });

        Assert.Equal(IntPtr.Zero, active);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
