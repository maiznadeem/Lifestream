namespace Lifestream.Data.Seed;

/// <summary>
/// A small, stable pseudo-random source (SplitMix64 seeding, xorshift64*). It is written out here, rather than using
/// <see cref="Random"/>, so the same seed gives the same numbers on every .NET version and machine.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed)
    {
        // SplitMix64 turns any seed (including 0) into a well-mixed non-zero state.
        var z = seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        _state = z ^ (z >> 31);
        if (_state == 0)
        {
            _state = 0x2545F4914F6CDD1DUL;
        }
    }

    public ulong NextUInt64()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>A number in [0, maxExclusive).</summary>
    public int Next(int maxExclusive) => (int)((NextUInt64() >> 11) % (ulong)maxExclusive);

    /// <summary>A number in [minInclusive, maxInclusive].</summary>
    public int Between(int minInclusive, int maxInclusive) => minInclusive + Next(maxInclusive - minInclusive + 1);

    /// <summary>True with the given probability (0 to 1).</summary>
    public bool Chance(double probability) => (NextUInt64() >> 11) / (double)(1UL << 53) < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];
}
