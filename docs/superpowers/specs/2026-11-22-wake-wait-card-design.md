# Sign-in wait card (2.1.9): design

Approved by the owner on 2026-11-22 from the mockup "Proposed sign-in waiting card".

## Why

After installing 2.1.8 the owner met a cold server, which went like this:
- the sign-in window showed one line of text for "starting the server", then another for "waiting for the database";
- the demo sign-in then failed with "Can't reach the server. Check your internet connection, then retry";
- the next click worked at once.

The cause: a sign-in clicked during a wake is one request that the server and Cloudflare hold through the whole wake. When the wake
outlasts the client's 120-second budget, the unanswered request is reported as "can't reach", which blames the network for a server
that was only slow. The owner wants the wait explained step by step, with animation, and no false error.

## Goals

- Never send the sign-in into a wake. A sign-in clicked while the server isn't known to be ready waits for the readiness check (short
  `/readyz` calls that answer within about 5 s once the container is up), then signs in at once.
- Show the wait as three steps, each with its own animation, an elapsed clock and a rough time:
  1. starting the server;
  2. waking the database;
  3. signing you in.
- "Can't reach the server" only when no connection can be made at all. A call that the server left unanswered for its whole budget is
  "taking longer than usual", never a network problem.

Non-goals: no change to the server, to the wake budget, or to the main window's waking notices.

Anti-goal: anything that looks like a failure while the wake is still within its usual time.

## Approaches

1. **Queue sign-in behind readiness, with a three-step card** (picked by the owner). There is no long-held sign-in request, so nothing
   can time out halfway, and the card can name each step from what the server actually said.
2. Keep sending the sign-in at once, correct only the timeout message, and animate the existing one-line banner. This is cheaper, but a
   long wake can still end in an error, and a sign-in held over 100 s meets Cloudflare's 524.

## Design

**API client**

When the call budget ends with the last attempt unanswered, the problem code is a new `ClientProblemCodes.NoAnswer`, not
`Unreachable`. Each place that maps codes to words handles it:
- screens say "The server is taking too long to answer";
- sign-in says the server is still waking, taking longer than usual.

**Sign-in view model (`LoginViewModel`)**

- Sign in, Try the demo and Retry first wait for readiness when the server isn't known to be ready. "Known ready" means it answered
  ready within the last `ServerStatus.ServerStaysUp` (4 minutes).
- The wait reuses a readiness check already running, or starts one.
- How the wait ends:
  - **ready:** send the sign-in;
  - **resting:** the "paused until the 1st" message;
  - **unreachable:** "Can't reach the server";
  - **still waking after the readiness rounds:** "still waking, taking longer than usual", with Retry to keep waiting.
- Cancel stops the wait as it stops a sign-in.
- The card's state:
  - **`WakeStep`:** `None`, `StartingServer`, `WakingDatabase` or `SigningIn`. It comes from `IServerStatus.Phase`: `ServerStarting` is
    step 1, and `ServerWorking` or `DatabaseWaking` is step 2. Step 3 applies while a sign-in that waited is being sent.
  - The elapsed time of the whole wait, and the time each finished step took.
  - The active step's progress towards its usual time: 45 s for the server, 60 s for the database, shown capped below full.
  - "Step n of 3", a title and a one-line explanation. Past `WakingTexts.LongerThanUsualAfter` the title says it's taking longer than usual.
- While the sign-in waits, the busy texts on the Sign in and Try the demo buttons say they will act when the server is ready.

**View (`LoginView.xaml`)**

- The one-line waking banner becomes a card, with the mockup's layout and the design system's tokens. It holds:
  - one illustration per active step, in a new control;
  - the title and the clock;
  - the explanation;
  - the three steps with their marks (pending dot, spinner, tick) and the active step's progress bar;
  - "Step n of 3" and Cancel.
- The illustrations:
  - **server:** a server stack whose lights blink in turn;
  - **database:** a database whose level rises with a ripple;
  - **sign-in:** the blood drop beating over a pulse line.
- Each animation runs forever only while `MotionSettings.AnimationsEnabled` is true. When it's false, as for the busy spinner, the
  pictures stand still.
- Screen readers: the card is a polite live region named after its title, and each step's text says whether that step is done, in
  progress or still to come.

## Left to the builder

The exact geometry, colours (design-system brushes only) and storyboard timings, the property names, and the test layout. Follow the
existing sign-in tests (`LoginWakeMessagesTests`, `LoginServerPillTests`) and the isolated real-app walk for proof.
