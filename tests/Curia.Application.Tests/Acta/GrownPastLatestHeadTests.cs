using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Domain.Primitives;
using Xunit;
using static Curia.Application.Tests.Acta.LogBuilder;

namespace Curia.Application.Tests.Acta;

/// <summary>
/// PR H (errata G67, R6.63): <see cref="ActaLog.GrownPast"/>, the one test a timer's <c>--if-grown</c> asks. Grown means an entry at or past the anchor's tree size,
/// other than <c>log.head</c> and <c>log.key</c> (or any such entry, with no anchor). The anchor here is the last head of the log: whether a head is the signer's own
/// is HSU40's question and not this table's (Amendment 3 ruling 34). Both sides in one table, so a predicate that is always true and one that is always false both fail it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class GrownPastLatestHeadTests
{
    /// <summary>
    /// HSU7. The rows, each with the answer it must give. The seam answers <c>true</c> for every log, so the not-grown rows are red there:
    /// <c>Expected: False  Actual: True</c>.
    /// </summary>
    [Fact]
    public void R6_63_HSU7_GrowthIsAnEntryOtherThanAHeadOrAKeyPastTheAnchor()
    {
        var rows = new (string Name, Entry[] Log, bool Grown)[]
        {
            ("an empty log", [], false),
            ("a post and no head", [Post()], true),
            ("a post, a key and a head covering both", [Post(), LogKey(), Head(2)], false),
            ("a post, a head covering it, and a key", [Post(), Head(1), LogKey()], false),
            ("a post, a head, and a post", [Post("a"), Head(1), Post("b")], true),
            ("a post, a head, and a declaration key", [Post(), Head(1), DeclarationKey()], true),
            ("a post, a head, and a compromise declaration nobody verified", [Post(), Head(1), Of("agent-a", "agent.key-compromised")], true),
            ("an enrolment pair, a head and a second head with nothing between", [Of("agent-a", "agent.enrolled"), Of("agent-a", "agent.key-bound"), Head(2), Head(3)], false),
        };

        var failures = new List<string>();
        foreach (var (name, log, grown) in rows)
        {
            var acta = ActaLog.Fold(LogOf(log)).Match(a => a, e => throw new InvalidOperationException(e.Type));
            SignedHead? anchor = acta.Heads.IsEmpty ? null : acta.Heads[^1];
            var got = acta.GrownPast(anchor);
            if (got != grown) failures.Add($"{name}: expected {grown}, got {got}");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
