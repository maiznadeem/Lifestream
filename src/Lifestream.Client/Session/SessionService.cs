using Lifestream.Contracts.Auth;
using Lifestream.Domain;

namespace Lifestream.Client.Session;

/// <summary>The signed-in session, issued by the API (SHELL-16). It is the only source of identity and role in the client.</summary>
public interface ISessionService
{
    SessionUser? User { get; }

    bool IsAuthenticated { get; }

    string? AccessToken { get; }

    string? RefreshToken { get; }

    /// <summary>Raised after <see cref="Establish"/> and after <see cref="Clear"/>.</summary>
    event EventHandler? Changed;

    /// <summary>Raised when the API refused to refresh the session, so the user must sign in again.</summary>
    event EventHandler? Expired;

    void Establish(TokenResponse tokens);

    /// <summary>
    /// Counts sign-ins and sign-outs. A refresh remembers the generation it started in and may only store its tokens if the
    /// session is still in that generation, so a refresh that finishes after sign-out can never bring the session back.
    /// </summary>
    long Generation { get; }

    /// <summary>Reads the tokens, the generation and the cancellation scope together, so they describe the same session.</summary>
    SessionSnapshot Snapshot();

    /// <summary>Stores refreshed tokens only if the session is still in the expected generation (compare and swap).</summary>
    bool TryUpdateTokens(TokenResponse tokens, long expectedGeneration);

    /// <summary>Forgets the user and both tokens, so the next call is anonymous.</summary>
    void Clear();

    /// <summary>Tells the app the session can no longer be refreshed. Ignored when the session has moved on since the generation.</summary>
    void Expire(long generation);

    bool IsInRole(Role role);
}

/// <summary>
/// The tokens, generation and cancellation scope of the session at one moment. <see cref="Scope"/> is cancelled when this
/// session ends (sign-out, or another user signing in), so work started under it can be abandoned.
/// </summary>
public readonly record struct SessionSnapshot(string? AccessToken, string? RefreshToken, long Generation, CancellationToken Scope = default);

public sealed class SessionService : ISessionService, IDisposable
{
    private readonly Lock _gate = new();
    private SessionUser? _user;
    private string? _access;
    private string? _refresh;
    private long _generation;
    private CancellationTokenSource _scope = new();

    public event EventHandler? Changed;

    public event EventHandler? Expired;

    public long Generation { get { lock (_gate) { return _generation; } } }

    public SessionUser? User { get { lock (_gate) { return _user; } } }

    public bool IsAuthenticated { get { lock (_gate) { return _access is not null; } } }

    public string? AccessToken { get { lock (_gate) { return _access; } } }

    public string? RefreshToken { get { lock (_gate) { return _refresh; } } }

    public void Establish(TokenResponse tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        CancellationTokenSource ended;
        lock (_gate)
        {
            _user = tokens.User;
            _access = tokens.AccessToken;
            _refresh = tokens.RefreshToken;
            _generation++;
            ended = _scope;
            _scope = new CancellationTokenSource();
        }

        ended.Cancel(); // anything still running for the previous session stops; it was not issued for this user
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SessionSnapshot(_access, _refresh, _generation, _scope.Token);
        }
    }

    public bool TryUpdateTokens(TokenResponse tokens, long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        lock (_gate)
        {
            if (_generation != expectedGeneration || _access is null)
            {
                return false; // signed out (or in again) while the refresh was in flight: the new tokens are discarded
            }

            _access = tokens.AccessToken;
            _refresh = tokens.RefreshToken;
            _user = tokens.User;
            return true;
        }
    }

    public void Clear()
    {
        CancellationTokenSource ended;
        lock (_gate)
        {
            _user = null;
            _access = null;
            _refresh = null;
            _generation++;
            ended = _scope;
            _scope = new CancellationTokenSource();
        }

        ended.Cancel(); // sign-out cancels every in-flight call of the session that ended (SHELL-16)
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Expire(long generation)
    {
        if (Generation == generation)
        {
            Expired?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsInRole(Role role) => User?.Role == role;

    public void Dispose() => _scope.Dispose();
}
