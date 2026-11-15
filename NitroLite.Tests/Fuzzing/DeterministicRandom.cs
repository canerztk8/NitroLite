namespace NitroLite.Tests.Fuzzing;

/// <summary>
/// A seeded generator whose sequence is fixed by this file alone.
/// </summary>
/// <remarks>
/// <see cref="Random"/> is deliberately not used: its algorithm is an implementation detail that has changed
/// between .NET versions, and a fuzz failure is only reproducible if the same seed produces the same 500 events on
/// every machine and every runtime. This is SplitMix64, written out so the sequence can never move under us.
/// </remarks>
internal sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(int seed)
    {
        // SplitMix64's own seeding step, so neighbouring seeds do not produce neighbouring sequences.
        _state = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x2545F4914F6CDD1DUL);
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A value in <c>[0, maxExclusive)</c>.</summary>
    public int Next(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextUInt64() % (ulong)maxExclusive);

    /// <summary>A value in <c>[minInclusive, maxInclusive]</c>.</summary>
    public int Next(int minInclusive, int maxInclusive) =>
        maxInclusive <= minInclusive ? minInclusive : minInclusive + Next(maxInclusive - minInclusive + 1);

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    public bool Chance(double probability) => NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];
}
