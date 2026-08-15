using CsCheck;
using Xunit;

namespace Curia.Domain.Primitives.Tests;

/// <summary>
/// <see cref="UlidGenerator"/>: the clock comes only from the injected <see cref="TimeProvider"/>
/// (CS-9), two calls within the same millisecond stay strictly ordered (the ULID spec's
/// monotonicity guidance), and -- the whole reason ULID exists over a random UUID for R8.3 --
/// generation order matches lexicographic sort order of the rendered strings, not merely of the
/// raw <see cref="Ulid.Value"/>. <see cref="UlidTests"/> covers <see cref="Ulid"/> itself.
/// </summary>
public sealed class UlidGeneratorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Ulid Require(Result<Ulid> result) =>
        result.Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));

    [Fact]
    public void NextEncodesTheInjectedClocksReadingNotAnAmbientOne()
    {
        var clock = new ManualTimeProvider(Epoch);
        var generator = new UlidGenerator(clock);

        var ulid = Require(generator.Next());

        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), ulid.TimestampMilliseconds);
    }

    [Fact]
    public void ConstructorRejectsANullClock() =>
        Assert.Throws<ArgumentNullException>(() => new UlidGenerator(null!));

    /// <summary>
    /// The monotonicity guarantee in its sharpest form: the clock never advances, yet 200
    /// consecutive calls still come out in strictly increasing order (spec: increment the 80-bit
    /// randomness component by 1 per same-millisecond call rather than redrawing).
    /// </summary>
    [Fact]
    public void MonotonicWithinASingleMillisecond()
    {
        var clock = new ManualTimeProvider(Epoch);
        var generator = new UlidGenerator(clock);

        var ulids = new List<Ulid>();
        for (var i = 0; i < 200; i++)
            ulids.Add(Require(generator.Next()));

        Assert.All(ulids, u => Assert.Equal(Epoch.ToUnixTimeMilliseconds(), u.TimestampMilliseconds));

        for (var i = 1; i < ulids.Count; i++)
        {
            Assert.True(ulids[i] > ulids[i - 1],
                $"call {i} ({ulids[i]}) did not sort strictly after call {i - 1} ({ulids[i - 1]})");
            Assert.True(string.CompareOrdinal(ulids[i].ToString(), ulids[i - 1].ToString()) > 0);
        }
    }

    [Fact]
    public void ANewMillisecondDrawsFreshRandomnessRatherThanContinuingTheIncrement()
    {
        var clock = new ManualTimeProvider(Epoch);
        var generator = new UlidGenerator(clock);

        var first = Require(generator.Next());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var second = Require(generator.Next());

        Assert.True(second > first);
        Assert.NotEqual(first.TimestampMilliseconds, second.TimestampMilliseconds);
    }

    /// <summary>
    /// Defensive hygiene, not a spec requirement of ULID itself: a clock reading before the Unix
    /// epoch is domain fallibility (CS-10), not a crash or a silently negative timestamp encoded
    /// into 48 unsigned bits.
    /// </summary>
    [Fact]
    public void NextFailsRatherThanEncodingATimestampBeforeTheUnixEpoch()
    {
        var beforeEpoch = new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero);
        var generator = new UlidGenerator(new ManualTimeProvider(beforeEpoch));

        var result = generator.Next();

        Assert.False(result.IsOk);
        var error = result.Match(_ => throw new Xunit.Sdk.XunitException("expected failure"), e => e);
        Assert.Equal("curia/domain-primitives/ulid/timestamp-out-of-range", error.Type);
    }

    /// <summary>
    /// CsCheck property: across a random sequence of small clock advances (including runs of
    /// zero-advance, i.e. same-millisecond calls), the order <see cref="UlidGenerator.Next"/> was
    /// called in always matches both the numeric order of <see cref="Ulid.Value"/> and the
    /// ordinal string order of <see cref="Ulid.ToString"/> -- lexicographic sort matching
    /// chronological/call order is ULID's entire reason for existing over a random UUID (R8.3).
    /// </summary>
    [Fact]
    public void GenerationOrderMatchesBothNumericAndLexicographicSortOrder() =>
        Gen.Int[0, 4].List[1, 60].Sample(deltasMs =>
        {
            var clock = new ManualTimeProvider(Epoch);
            var generator = new UlidGenerator(clock);

            var ulids = new List<Ulid>(deltasMs.Count);
            foreach (var deltaMs in deltasMs)
            {
                clock.Advance(TimeSpan.FromMilliseconds(deltaMs));
                ulids.Add(Require(generator.Next()));
            }

            for (var i = 1; i < ulids.Count; i++)
            {
                if (ulids[i] <= ulids[i - 1])
                    return false;
                if (string.CompareOrdinal(ulids[i].ToString(), ulids[i - 1].ToString()) <= 0)
                    return false;
            }

            return true;
        }, iter: 500);
}
