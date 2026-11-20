# Real windows on an isolated desktop

Real windows (the RealWindow tests, the real app under `tests/realapp`) must never run on the desktop of the person using the PC: on
2026-11-20 "off-screen, never activated" windows in the owner's session still took the keyboard and flashed on the monitor. Here they run on
a desktop of their own, created with `CreateDesktop` in the same window station and never switched to. Only one desktop receives input and is
shown (the input desktop); windows on another desktop cannot get its input, its foreground or its focus, and window messages and hooks do
not cross desktops (Microsoft Learn, "Desktops").

- `Lifestream.Isolated hold <state>` creates the desktop and a job object for everything started there, and watches the person's input
  desktop every 10 ms (foreground, focus, caret, capture, menus, cursor shape, every top-level window) and through a WinEvent hook. Anything
  of ours there kills the whole job at once and fails the run (exit 3). It only reads; it creates no window and sends no input.
- `Lifestream.Isolated exec <state> [--env K=V] [--cwd D] [--timeout S] -- exe args` starts a process on that desktop, in the job, with
  `STARTF_FORCEOFFFEEDBACK` (without it the "app starting" cursor shows on the person's screen for ~1.5 s).
- `Lifestream.Isolated stop <state>` ends it and prints the monitor's summary.
- `Lifestream.IsolationProbe` is the proof: windows, dialogs, popups, Focus/Activate/SetForegroundWindow and UI Automation for two minutes.

What works there: WPF windows, dialogs, popups, menus, maximize, activation and keyboard focus inside that desktop, UI Automation (each
action takes ~2 s to arrive), and `PrintWindow(PW_RENDERFULLCONTENT)` - but only with software rendering: with hardware rendering the client
area comes back empty. `GetForegroundWindow` is always NULL there and `SetForegroundWindow` always fails.

Callers: `tools/test-fast.ps1 -IsolatedDesktop` (the real-window tests) and `tools/realapp-isolated.ps1` (the real-app harness).
Start the tools with `DOTNET_ROOT` pointing at the .NET 11 SDK when it is not the machine's.
