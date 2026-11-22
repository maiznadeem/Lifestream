# Sign-in wait card Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A sign-in clicked during a cold wake waits for the readiness check instead of being held through the wake. The wait shows as an
animated three-step card, and a call the server left unanswered is never reported as "can't reach".

**Architecture:**
- One new client problem code for budget-end timeouts.
- `LoginViewModel` gains a readiness gate in front of its sign-in calls, plus the card's state, derived from `IServerStatus.Phase`.
- `LoginView.xaml` replaces the one-line banner with a card. A new `WakeStepArt` control draws one looping illustration per step.

**Tech Stack:** .NET 11, WPF, CommunityToolkit.Mvvm, xUnit v3 with the repository's fakes (`HoldingServer`, `ScriptedHandler`,
`ManualTimeProvider`, `TestUiThread`, `Rig`, `Hosted`/`StaHost`).

**Spec:** `docs/superpowers/specs/2026-11-22-wake-wait-card-design.md`

## Global Constraints

- **Wording:**
  - "Can't reach the server" only for `ClientProblemCodes.Unreachable`, meaning no connection at all.
  - "Longer than usual" only after `WakingTexts.LongerThanUsualAfter` (100 s).
- **Animations:** loop only while `MotionSettings.AnimationsEnabled` is true; otherwise they stand still, like `SpinnerRepeat`.
- **Styling:** design-system brushes and tokens only (`Brush.*`, `Inset.*`, `Radius.*`, `Text.*`), no literal colours. The card works in
  light, dark and high contrast.
- **Element names:** keep `WakingBanner` (now the card) and `CancelButton`, because existing render tests find them.
- **Gate:** warnings fail the build, so the fast gate must pass:
  `tools/test-fast.ps1 -Worktree <wt> -AllowRealWindowSkip -Dotnet G:\Claude\workspace\Lifestream\tools\dotnet\dotnet.exe`, with
  `LIFESTREAM_REAL_WINDOW_TESTS=0`.
- **Desktop UI:** check it only on the isolated desktop (`tools/realapp-isolated.ps1`), never on the owner's desktop.

## Review Focus

1. Sign-in clicked while no readiness check has run (Server `Checking`, nothing in flight) goes straight to the POST, as today. Pinned in Task 2.
2. Sign-in clicked after the window sat "Server ready" for over 4 minutes (the server has gone back to sleep) waits for readiness first. Pinned in Task 2.
3. Cancel during the readiness wait sends no sign-in, and the buttons work again. Pinned in Task 2.
4. A readiness check that ends unreachable, or resting, during the wait ends the sign-in with that message and sends no POST. Pinned in Task 2.
5. The card's clock and its step times keep counting across the readiness check and the sign-in that follows: one wake shows as one
   card, not a reset at step 3. Pinned in Task 3.

---

### Task 1: Budget-end is "no answer", not "unreachable"

**Files:**
- Modify: `src/Lifestream.Client/Api/ApiResult.cs` (add `ClientProblemCodes.NoAnswer = "client_no_answer"` and `ApiProblem.IsNoAnswer`)
- Modify: `src/Lifestream.Client/Api/LifestreamApiClient.cs:468-473` (the `OperationCanceledException` branch returns `NoAnswer`)
- Modify: `src/Lifestream.Client/Common/ViewModelBase.cs:501-512` (`NoAnswer => TimeoutMessage`)
- Modify: `src/Lifestream.Client/Features/ListScreens/Shared.cs:39` (`NoAnswer` maps to "The server is taking too long to answer. Try again in a moment.")
- Modify: `src/Lifestream.Client/Auth/LoginViewModel.cs` `Fail` (`NoAnswer` maps to `LoginProblem.ServerWaking`, with the existing "still waking ... longer than usual" text)
- Test: `tests/Lifestream.Client.Tests/WakeWaitTests.cs`, `LoginWakeMessagesTests.cs`

- [ ] In `TheBudget_IsTheOnlyTimeLimit_AnAttemptStillUnansweredAfter120s_IsReportedAsNoAnswer`, change the assertion to `Assert.Equal(ClientProblemCodes.NoAnswer, result.Problem!.Code)` and `Assert.False(result.Problem.IsUnreachable)`.
- [ ] Add `LoginWakeMessagesTests.SignIn_HeldPastItsBudget_SaysStillWaking_NeverCantReach`. With a `HoldingServer` that never answers, press Demo and pass 121 s. Assert `Problem == LoginProblem.ServerWaking`, that `Message` contains "longer than usual", and that `vm.Server != ServerReadiness.Unreachable`.
- [ ] Run both tests; they must fail.
- [ ] Implement, then run `Lifestream.Client.Tests.exe -class Lifestream.Client.Tests.WakeWaitTests -class Lifestream.Client.Tests.LoginWakeMessagesTests`; it must pass.
- [ ] Commit: "A call the server left unanswered is not 'can't reach'".

### Task 2: Sign-in waits for readiness

**Files:** Modify `src/Lifestream.Client/Auth/LoginViewModel.cs`. Test: new `tests/Lifestream.Client.Tests/LoginWaitsForReadinessTests.cs`
(reuse the `Screen`/`Open`/`PassAsync` helpers by copying them; they are private to `LoginWakeMessagesTests`).

**Interfaces:**
- Produces:
  - `internal bool NeedsReadinessFirst` (true when a readiness check is running, or `Server` is `Waking` or `Unreachable`, or the last
    `Ready` is older than `ServerStatus.ServerStaysUp`);
  - `public bool IsWaitingForServer` (true while a sign-in waits on readiness; raises `PropertyChanged`);
  - `public string SignInBusyText` / `DemoBusyText`: "Signing in when the server is ready" and "Opening the demo when the server is ready"
    while waiting, otherwise "Signing in" and "Opening the demo".

Approach:
- `CheckServerAsync` keeps its task in `_readinessCheck`.
- `AuthenticateAsync` awaits `_readinessCheck ?? CheckServerAsync(ct)` (cancellable through the attempt's token: wrap it with `WaitAsync(ct)`) when `NeedsReadinessFirst`.
- After the wait it does one of these:
  - `Ready`: send;
  - `Resting`: `Fail(DatabaseResting)`;
  - `Unreachable`: `Fail(Unreachable)`;
  - `Waking`: `Fail(DatabaseWaking)`.
- Record `_readyAt = Clock.GetTimestamp()` whenever `Server` becomes `Ready`.

- [ ] Write the tests, each against a `ScriptedHandler` that counts requests by path:
  - `Demo_WhileTheReadinessCheckSaysWaking_SendsNoSignIn_UntilReady_ThenSignsInOnce`: `/readyz` answers 503 `database_waking` (Retry-After 2) three times, then 200 `{"database":"ready"}`. Start `CheckServerAsync`, press Demo, and pass time. Assert no POST to `auth/demo` before the 200, exactly one after, and that the session is established.
  - `Demo_WhenTheReadinessCheckEndsUnreachable_FailsAsUnreachable_AndSendsNoSignIn`.
  - `Demo_WhenTheDatabaseIsResting_SaysResting_AndSendsNoSignIn`.
  - `Cancel_WhileWaitingForReadiness_SendsNoSignIn_AndTheButtonsWorkAgain`.
  - `SignIn_WithNoReadinessCheckRun_GoesStraightToTheServer`: the Review Focus 1 case.
  - `SignIn_AfterServerReadyForMoreThanFourMinutes_ChecksReadinessFirst`: the Review Focus 2 case.
  - `BusyTexts_SayWhenTheServerIsReady_WhileWaiting`.
- [ ] Run them; they must fail.
- [ ] Implement.
- [ ] Run the new class plus `LoginWakeMessagesTests`, `LoginServerPillTests` and `LoginRealClientTests`. Fix any existing test whose expectation changes only because the sign-in now waits for readiness, with a comment saying why.
- [ ] Commit: "Sign-in waits for the readiness check instead of riding the wake".

### Task 3: The card's state

**Files:** Modify `src/Lifestream.Client/Auth/LoginViewModel.cs`, and `src/Lifestream.Client/Common/OperationRunner.cs` (texts in `WakingTexts`).
Test: new `tests/Lifestream.Client.Tests/LoginWakeCardTests.cs`.

**Interfaces:**
- Produces:
  - `public enum WakeStep { None, StartingServer, WakingDatabase, SigningIn }`;
  - `WakeStep Step`;
  - `bool IsWakeCardVisible` (`Step != None`);
  - `string WakeTitle`;
  - `string WakeExplanation`;
  - `string WakeClock` ("m:ss" since the wake began);
  - `string StepOfText` ("Step 2 of 3");
  - `double StepProgress` (0..0.95);
  - three step rows, `IReadOnlyList<WakeStepRow> Steps`, where
    `record WakeStepRow(string Label, string Hint, WakeStepState State, string? Took)` and
    `enum WakeStepState { Pending, Active, Done }`.
- Values:
  - **Labels:** "Starting the server" / "Started the server", "Waking the database" / "Woke the database", "Signing you in".
  - **Hints:** "about 30–45 s" and "about 1 min".
  - **Usual times:** 45 s and 60 s.
  - **Titles:** "Getting the server ready", "Signing you in", or "Still waking the database" / "Still starting the server" past `LongerThanUsualAfter`.
  - **Explanations:**
    - step 1: "The free server sleeps when nobody is using it. Waking it takes about a minute and a half. Fill in your details meanwhile: you'll be signed in as soon as it's ready."
    - step 2: "The server is up. Its database is waking from its pause, which takes about a minute."
    - step 3: "The server is ready. Opening the demo clinic." or "The server is ready. Signing you in."
    - late: "This is taking longer than usual. Keep waiting: you'll be signed in when it's ready."
- Rules:
  - **Step from the phase:** while `IsServerWaking`, `ServerStarting` is step 1, and `ServerWorking` or `DatabaseWaking` is step 2.
  - **Step 3:** while a sign-in that waited (or a slow one) is being sent and the server is no longer waking.
  - **Clock:** a 1-s timer on `Clock` drives `WakeClock` and `StepProgress` while the card shows. It counts from `_wakingBegan`, which already spans one wake.
  - **Done times:** a step's `Took` is recorded when the step after it starts.

- [ ] Write the tests with the `Screen` helpers and a `HoldingServer`:
  - `Card_FollowsTheWake_StartingServer_ThenDatabase_ThenSigningIn_WithStepTimes`: the server answers `/readyz` 503 `database_waking` at 34 s, then 200 at 89 s, then the demo POST.
  - `Card_PastTheUsualTime_SaysStill_AndNeverShowsAProblem`.
  - `Card_HiddenWhenTheServerIsReady_AndNoSignInWaits`.
  - `Card_Clock_IsOneWake_AcrossTheReadinessCheckAndTheSignIn`: the Review Focus 5 case.
- [ ] Run them; they must fail.
- [ ] Implement, then run them; they must pass.
- [ ] Commit: "The sign-in window's wait card state".

### Task 4: The card and its animations

**Files:**
- Create: `src/Lifestream.Desktop/Controls/WakeStepArt.xaml(.cs)`. It is a `UserControl` with a `Step` dependency property (`WakeStep`) and
  three `Canvas` drawings: the server stack with three lights, the database with its level and ripple, and the blood drop with its pulse
  line. Storyboards start on `Loaded` and when `Step` changes, with `RepeatBehavior` set to `MotionSettings.SpinnerRepeat`, and stop on `Unloaded`.
- Modify: `src/Lifestream.Desktop/Auth/LoginView.xaml:100-112`: the `WakingBanner` becomes the card, laid out as in the mockup and bound to Task 3's properties.
  - The step rows use an `ItemsControl` over `Steps`. Each mark is a dot, a spinner (the existing busy-spinner style) or a tick.
  - The active row shows a `ProgressBar` bound to `StepProgress`.
  - The buttons' `ButtonAssist.BusyText` binds to `SignInBusyText` and `DemoBusyText`.
  - Each step row's automation name says "<label>: done in <took>" / "in progress" / "to come".
- Modify: `tests/Lifestream.Desktop.Tests/LoginWakingRenderedTests.cs`: assert the card shows the title "Getting the server ready", "Step 1 of 3" and Cancel, and capture `LoginWindow_Waking.png`.
- Test: add `LoginWakeCardRenderedTests.Card_EachStep_RendersItsArt_AndAnimationsStandStillWhenAnimationsAreOff`. It renders steps 1, 2 and 3 off-screen (`MotionSettings.Override = false` and then `true`), checks that the art for that step is the visible one and the others are collapsed, and captures one PNG per step.

- [ ] Write the render test and the updated banner test; they must fail.
- [ ] Implement.
- [ ] Run the fast gate; it must pass.
- [ ] Look at the three captures and compare them with the mockup.
- [ ] Commit: "The sign-in wait card with an animation for each step".

### Task 5: Prove it in the real app and ship 2.1.9

- [ ] Bump the version to 2.1.9. The memory note "Lifestream overhaul run" lists every file; add a CHANGELOG section with "### Fixed" and "### Look and feel".
- [ ] Build Release and run the real app on the isolated desktop against the live API from fully cold (`scratch/wake-ux/cold.sh` first). Capture every 10 s from the demo click to the dashboard. The card must go from step 1 to 2 to 3, show no problem row, and end on the dashboard with data.
- [ ] Force a demo reset (`scratch/wake-ux/force-reset.sh`) while signed in, then switch screens. Exactly one "Demo data was reset" toast, and the screen loads.
- [ ] Run the fast gate, open the PR, wait for its checks, merge, then run `tools/release-local.ps1 -Version 2.1.9` and confirm the release is in the update feed.
