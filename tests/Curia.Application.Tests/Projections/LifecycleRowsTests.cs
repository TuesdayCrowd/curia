using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Xunit;
using static Curia.Application.Tests.Acta.LogBuilder;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// PR H (errata G67, Amendment 2 ruling 28): <see cref="LifecycleRows"/>, the one predicate for a lifecycle row beyond enrolment pairs, which the start-up scan
/// (<c>LifecycleEntryScan</c>, EventId 7006) and the head signer's stop both call, so that they cannot count differently. The scan's own GF10 probes stay green and unedited:
/// that is the control that the extraction changed no count.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleRowsTests
{
    private const string Enrolled = "agent.enrolled";
    private const string Bound = "agent.key-bound";

    private static string Rows(params Entry[] log) =>
        string.Join("; ", LifecycleRows.Find(LogOf(log)).Select(r => $"{r.LogIndex} {r.Type.Value} {r.Aggregate.Value}"));

    /// <summary>
    /// HSU39. A table over streams. Green once the predicate is extracted (the seam extracts it, so this is a control there): a lone statement-less
    /// <c>agent.key-bound</c> onto <c>[enrolled]</c> is the scan's stated undercount and is written as a row, so that a change to it is seen.
    /// </summary>
    [Fact]
    public void R6_63_HSU39_OnePredicateCountsALifecycleRowBeyondEnrolmentPairs()
    {
        var got = new List<string>
        {
            "pair: " + Rows(Of("a", Enrolled), Of("a", Bound)),
            "lone binding onto an enrolment (the undercount): " + Rows(Of("a", Enrolled), Of("a", Bound)),
            "a statement-bearing binding after the pair: " + Rows(Of("a", Enrolled), Of("a", Bound), Of("a", Bound)),
            "retired: " + Rows(Post(), Of("a", LifecycleAppendGuard.KeyRetiredType)),
            "compromised: " + Rows(Of("a", LifecycleAppendGuard.KeyCompromisedType)),
            "a binding on a stream that did not begin enrolled: " + Rows(Of("a", "agent.owner-attested"), Of("a", Bound)),
            "the index is the position in the sequence given: " + Rows(Post("p"), Of("a", Enrolled), Of("a", Bound), Of("a", LifecycleAppendGuard.KeyRetiredType)),
            "heads, keys and a declaration key are no row: " + Rows(Post(), LogKey(), DeclarationKey(), Head(3)),
        };

        Assert.Equal(
            [
                "pair: ",
                "lone binding onto an enrolment (the undercount): ",
                "a statement-bearing binding after the pair: 2 agent.key-bound a",
                "retired: 1 agent.key-retired a",
                "compromised: 0 agent.key-compromised a",
                "a binding on a stream that did not begin enrolled: 1 agent.key-bound a",
                "the index is the position in the sequence given: 3 agent.key-retired a",
                "heads, keys and a declaration key are no row: ",
            ],
            got);
    }

    private static string Tail(Func<ActaLog, SignedHead?> anchor, params Entry[] log)
    {
        var acta = ActaLog.Fold(LogOf(log)).Match(a => a, e => throw new InvalidOperationException(e.Type));
        return string.Join("; ", acta.LifecycleRowsPast(anchor(acta)).Select(r => $"{r.LogIndex} {r.Type.Value}"));
    }

    /// <summary>
    /// HSU39's tail table (Amendment 3 ruling 35): <see cref="ActaLog.LifecycleRowsPast"/> measures from the anchor's signed <c>tree_size</c> and never from its log index. No anchor: every
    /// row. An anchor with <c>tree_size</c> 1 at log index 3 over <c>[post, retirement, retirement, head]</c>: the two rows at 1 and 2. An anchor covering everything: none. And a head of
    /// <c>tree_size</c> 3 at index 4, which a row appended between its fold and its append precedes: that row, at 3, is in the tail (red on FHSU37, a tail from the head's log index).
    /// </summary>
    [Fact]
    public void R6_63_HSU39_TheTailIsMeasuredFromTheAnchorsSignedTreeSize()
    {
        var retired = LifecycleAppendGuard.KeyRetiredType;
        var got = new List<string>
        {
            "no anchor: " + Tail(_ => null, Post(), Of("a", retired), Of("a", retired), Head(1)),
            "an anchor of tree_size 1 at log index 3: " + Tail(a => a.Heads[0], Post(), Of("a", retired), Of("a", retired), Head(1)),
            "an anchor covering everything: " + Tail(a => a.Heads[0], Post(), Of("a", retired), Head(2)),
            "a head of tree_size 3 at log index 4, a row between: " + Tail(a => a.Heads[0], Post(), Of("a", retired), Of("a", retired), Of("a", retired), Head(3)),
        };

        Assert.Equal(
            [
                "no anchor: 1 agent.key-retired; 2 agent.key-retired",
                "an anchor of tree_size 1 at log index 3: 1 agent.key-retired; 2 agent.key-retired",
                "an anchor covering everything: ",
                "a head of tree_size 3 at log index 4, a row between: 3 agent.key-retired",
            ],
            got);
    }
}
