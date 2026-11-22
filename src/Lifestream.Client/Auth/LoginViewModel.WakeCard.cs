using Lifestream.Client.Api;
using Lifestream.Client.Common;

namespace Lifestream.Client.Auth;

/// <summary>The part of a cold wake the sign-in window's card is showing.</summary>
public enum WakeStep
{
    None,
    StartingServer,
    WakingDatabase,
    SigningIn,
}

public enum WakeStepState
{
    Pending,
    Active,
    Done,
}

/// <summary>One row of the wait card: the step's words, its usual time, where it stands and, once done, how long it took.</summary>
public sealed record WakeStepRow(string Label, string Hint, WakeStepState State, string? Took)
{
    public bool IsDone => State == WakeStepState.Done;

    public bool IsActive => State == WakeStepState.Active;

    public bool IsPending => State == WakeStepState.Pending;

    /// <summary>True for the steps that have a usual time (and so a progress bar while active): starting the server and waking the database.</summary>
    public bool IsHinted => Hint.Length > 0;

    /// <summary>What a screen reader says for the row (also its text where a list names its items, never the record's C# form).</summary>
    public string AutomationName => State switch
    {
        WakeStepState.Done => Took is null ? $"{Label}: done" : $"{Label}: done in {Took}",
        WakeStepState.Active => $"{Label}: in progress",
        _ => $"{Label}: to come",
    };

    public override string ToString() => AutomationName;
}

/// <summary>
/// The wait card of the sign-in window (2.1.9): a cold wake shown as three steps, starting the server, waking the database and signing in,
/// with a clock for the whole wait, the time each finished step took and the active step's progress towards its usual time. The steps
/// follow what the server said (<see cref="IServerStatus.Phase"/>); step 3 is a sign-in that waited for the server and is now being sent.
/// One wake is one card: the readiness check, the click and the sign-in that follows keep its clock.
/// </summary>
public sealed partial class LoginViewModel
{
    /// <summary>The usual time of step 1 and step 2, measured through Cloudflare on 2026-11-22 (container 30 to 46 s, database about a minute).</summary>
    public static readonly TimeSpan UsualServerStart = TimeSpan.FromSeconds(45);

    public static readonly TimeSpan UsualDatabaseWake = TimeSpan.FromSeconds(60);

    /// <summary>The progress bar of a step never shows full while the step is still going on.</summary>
    public const double MaxStepProgress = 0.95;

    private bool _signingInAfterWait;
    private long? _readinessStartedAt;
    private long? _cardBegan;
    private long? _stepBegan;
    private long? _cardHiddenAt;
    private readonly string?[] _took = new string?[2];
    private ITimer? _cardTicker;

    /// <summary>The step the card shows; <see cref="WakeStep.None"/> when there is no card.</summary>
    public WakeStep Step { get; private set; }

    public bool IsWakeCardVisible => Step != WakeStep.None;

    /// <summary>The time since the wake began, "m:ss".</summary>
    public string WakeClock => _cardBegan is { } began && Step != WakeStep.None ? Format(Clock.GetElapsedTime(began)) : "0:00";

    public string StepOfText => Step == WakeStep.None ? string.Empty : $"Step {(int)Step} of 3";

    public string WakeTitle => Step switch
    {
        WakeStep.SigningIn => WakingTexts.CardSigningIn,
        WakeStep.StartingServer when IsCardLate => WakingTexts.CardLateServer,
        WakeStep.WakingDatabase when IsCardLate => WakingTexts.CardLateDatabase,
        _ => WakingTexts.CardTitle,
    };

    public string WakeExplanation => Step switch
    {
        WakeStep.SigningIn => _signingInWithPassword ? WakingTexts.CardReadySignIn : WakingTexts.CardReadyDemo,
        _ when IsCardLate => WakingTexts.CardLate,
        WakeStep.StartingServer => WakingTexts.CardServer,
        WakeStep.WakingDatabase => WakingTexts.CardDatabase,
        _ => string.Empty,
    };

    /// <summary>How far the active step is towards its usual time, 0 to <see cref="MaxStepProgress"/>; 0 for step 3, which has no bar.</summary>
    public double StepProgress
    {
        get
        {
            var usual = Step switch { WakeStep.StartingServer => UsualServerStart, WakeStep.WakingDatabase => UsualDatabaseWake, _ => TimeSpan.Zero };
            return usual == TimeSpan.Zero || _stepBegan is not { } began ? 0 : Math.Min(MaxStepProgress, Clock.GetElapsedTime(began) / usual);
        }
    }

    /// <summary>The three rows of the card. Replaced only when a step or its time changes, never on the clock's tick: a new list makes the
    /// view rebuild the rows, which restarts the active step's spinner and the rows screen readers are on.</summary>
    public IReadOnlyList<WakeStepRow> Steps { get; private set; } = [];

    private IReadOnlyList<WakeStepRow> BuildRows() =>
    [
        Row(WakeStep.StartingServer, "Starting the server", "Started the server", WakingTexts.CardServerHint, _took[0]),
        Row(WakeStep.WakingDatabase, "Waking the database", "Woke the database", WakingTexts.CardDatabaseHint, _took[1]),
        Row(WakeStep.SigningIn, "Signing you in", "Signed in", string.Empty, null),
    ];

    private bool IsCardLate => _cardBegan is { } began && Clock.GetElapsedTime(began) >= WakingTexts.LongerThanUsualAfter;

    private WakeStepRow Row(WakeStep step, string label, string doneLabel, string hint, string? took)
    {
        var state = Step > step ? WakeStepState.Done : Step == step ? WakeStepState.Active : WakeStepState.Pending;
        return new WakeStepRow(state == WakeStepState.Done ? doneLabel : label, hint, state, state == WakeStepState.Done ? took : null);
    }

    private static string Format(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private WakeStep NextStep() =>
        _signingInAfterWait && IsBusy ? WakeStep.SigningIn
        : IsServerWaking || IsWaitingForServer ? (_phase == WakePhase.ServerStarting ? WakeStep.StartingServer : WakeStep.WakingDatabase)
        : WakeStep.None;

    /// <summary>Moves the card to the step the screen's state says, recording the time each step took. A card never goes back a step.</summary>
    private void RefreshCard()
    {
        var next = NextStep();
        var now = Clock.GetTimestamp();
        if (next == WakeStep.None)
        {
            if (Step != WakeStep.None)
            {
                Step = WakeStep.None;
                _cardHiddenAt = now;
                _cardTicker?.Dispose();
                _cardTicker = null;
                RaiseCard();
            }

            return;
        }

        if (Step == WakeStep.None)
        {
            // A card that comes back within moments (the readiness check ended, the sign-in starts) is the same wake: its clock goes on.
            var sameWake = _cardBegan is not null && _cardHiddenAt is { } hidden && Clock.GetElapsedTime(hidden, now) <= SameWakeWithin;
            if (!sameWake)
            {
                _cardBegan = _readinessCheck is { IsCompleted: false } && _readinessStartedAt is { } started ? started : now;
                _stepBegan = _cardBegan;
                _took[0] = _took[1] = null;
            }

            _cardTicker?.Dispose();
            _cardTicker = Clock.CreateTimer(_ => OnUiThread(RaiseTick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        if (next > Step)
        {
            if (Step is WakeStep.StartingServer or WakeStep.WakingDatabase && _stepBegan is { } began)
            {
                _took[(int)Step - 1] = Format(Clock.GetElapsedTime(began, now));
            }

            if (Step != WakeStep.None)
            {
                _stepBegan = now;
            }

            Step = next;
        }

        RaiseCard();
    }

    private void OnUiThread(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    /// <summary>The clock's tick: the time, the progress and, past the usual time, the words; never the rows.</summary>
    private void RaiseTick()
    {
        OnPropertyChanged(nameof(WakeClock));
        OnPropertyChanged(nameof(StepProgress));
        OnPropertyChanged(nameof(WakeTitle));
        OnPropertyChanged(nameof(WakeExplanation));
    }

    private void RaiseCard()
    {
        Steps = BuildRows();
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(IsWakeCardVisible));
        OnPropertyChanged(nameof(WakeClock));
        OnPropertyChanged(nameof(StepOfText));
        OnPropertyChanged(nameof(WakeTitle));
        OnPropertyChanged(nameof(WakeExplanation));
        OnPropertyChanged(nameof(StepProgress));
        OnPropertyChanged(nameof(Steps));
    }
}
