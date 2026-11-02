using System.Diagnostics.CodeAnalysis;
using Curia.Application.Retrieval;
using Xunit;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// AFC16(a) (errata G55, R8.84): the in-flight registration is counted. The confirm of a faulted append and the index step each register the digest through
/// <see cref="IndexSweep.Track"/>, and the end of one must not clear the other's (trap 102).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexSweepInFlightTests
{
    private const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000d04";

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string State(IndexSweep sweep) => sweep.IsIndexing(Digest) ? "in flight" : "not in flight";

    /// <summary>
    /// AFC16(a). Two scopes of one digest nest: with the inner one released the digest is still in flight; with both released it is not; releasing a scope twice releases
    /// once; and the registration through <c>BeginIndexing</c> and <c>EndIndexing</c> nests the same way.
    /// </summary>
    [Fact]
    public void R8_84_AFC16a_TwoRegistrationsOfOneDigestNestAndOneReleaseDoesNotClearBoth()
    {
        var sweep = new IndexSweep(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var outer = sweep.Track(Digest);
        var inner = sweep.Track(Digest);
        inner.Dispose();
        var afterInner = State(sweep);
        inner.Dispose();
        var afterInnerTwice = State(sweep);
        outer.Dispose();
        var afterBoth = State(sweep);

        sweep.BeginIndexing(Digest);
        sweep.BeginIndexing(Digest);
        sweep.EndIndexing(Digest);
        var afterOneEnd = State(sweep);
        sweep.EndIndexing(Digest);

        Said(
            "inner scope released: in flight | the same scope released again: in flight | both released: not in flight | two begins and one end: in flight | the second end: not in flight",
            $"inner scope released: {afterInner} | the same scope released again: {afterInnerTwice} | both released: {afterBoth} | two begins and one end: {afterOneEnd} | the second end: {State(sweep)}");
    }
}
