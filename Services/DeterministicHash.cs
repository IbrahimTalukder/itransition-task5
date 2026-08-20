namespace MovieStoreShowcase.Services;

/// <summary>
/// Produces a stable 32-bit seed from (userSeed, recordIndex, salt).
/// .NET's built-in string.GetHashCode() is randomized per process run,
/// so we can't rely on it for reproducibility across restarts/devices.
/// FNV-1a is simple, stable, and good enough for RNG seeding here.
/// </summary>
public static class DeterministicHash
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    public static int Combine(long userSeed, long recordIndex, string salt)
    {
        unchecked
        {
            uint hash = FnvOffset;

            void Mix(long value)
            {
                var bytes = BitConverter.GetBytes(value);
                foreach (var b in bytes)
                {
                    hash ^= b;
                    hash *= FnvPrime;
                }
            }

            void MixString(string s)
            {
                foreach (var c in s)
                {
                    hash ^= c;
                    hash *= FnvPrime;
                }
            }

            // MAD-style combine of seed and index, then fold in the salt
            // so different purposes (core fields / likes / reviews / trailer)
            // never collide even for the same movie.
            Mix(userSeed);
            Mix(recordIndex);
            MixString(salt);

            return (int)(hash & 0x7FFFFFFF);
        }
    }

    /// <summary>Stable uniform double in [0,1) derived from the same inputs.</summary>
    public static double NextUniform(long userSeed, long recordIndex, string salt)
    {
        var seed = Combine(userSeed, recordIndex, salt);
        return new Random(seed).NextDouble();
    }

    /// <summary>
    /// Pavel's "fractional times" technique (from the Discord hints): applies
    /// <paramref name="fn"/> to an accumulator floor(n) times for certain, then
    /// one more time with probability frac(n) - so n=2.7 means "2 times for
    /// sure, a 3rd time with 70% probability". Deterministic given the same
    /// (userSeed, recordIndex, salt): the probabilistic "extra" call is decided
    /// by a single stable draw, not System/Math.Random.
    /// </summary>
    public static T FractionalTimes<T>(long userSeed, long recordIndex, string salt, double n, T seed, Func<T, T> fn)
    {
        if (n < 0) throw new ArgumentException("n cannot be negative", nameof(n));

        int whole = (int)Math.Floor(n);
        T result = seed;
        for (int i = 0; i < whole; i++) result = fn(result);

        double frac = n - whole;
        double draw = NextUniform(userSeed, recordIndex, salt);
        if (draw < frac) result = fn(result);

        return result;
    }
}