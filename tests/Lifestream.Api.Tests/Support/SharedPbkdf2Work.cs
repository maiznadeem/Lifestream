using System.Collections.Concurrent;
using Lifestream.Api.Auth;

namespace Lifestream.Api.Tests.Support;

/// <summary>
/// The real PBKDF2 hasher (600,000 iterations, about 0.3 s of CPU per call), with the work shared across the test run. Verifying a password against a
/// hash is a pure function, so a pair already verified in this process gets the same answer without the work again: the hundreds of logins of the
/// suite check the same few hashes (the shared test hash, each host's dummy hash) with the same few passwords. <see cref="Hash"/> is never shared
/// (every call gets a new random salt). The host's dummy hash for unknown names is one object for the whole run (<see cref="DummyHash"/>).
/// Hosts whose PBKDF2 work is itself under test (the counted hosts of the timing tests) register their own hasher instead.
/// </summary>
public sealed class SharedPbkdf2Work : ICredentialHasher
{
    private readonly Pbkdf2CredentialHasher _inner = new();
    private readonly ConcurrentDictionary<(string Hash, string Password), Lazy<bool>> _verified = new();

    private SharedPbkdf2Work()
    {
        Dummy = new DummyHash(this);
    }

    public static SharedPbkdf2Work Instance { get; } = new();

    /// <summary>One dummy hash for every host of the run (each host computing its own costs one PBKDF2 hash per host).</summary>
    public DummyHash Dummy { get; }

    public string Hash(string password) => _inner.Hash(password);

    public bool Verify(string hash, string password) =>
        _verified.GetOrAdd((hash, password), key => new Lazy<bool>(() => _inner.Verify(key.Hash, key.Password))).Value;
}
