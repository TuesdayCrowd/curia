using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Retrieval;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Retrieval;

// PIL33: constructs the production type as the object under test, not as a source of expectation.

/// <summary>
/// Errata G51 (R11.69) at the backlog: what the process holds of a post it committed and could not index. PIL11. The bound is read from the entry
/// (<see cref="PublishedIndexLag.BacklogBound"/>), never from the code's constant.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexBacklogTests
{
    private static readonly DateTimeOffset Due = new(2026, 10, 31, 12, 0, 5, TimeSpan.Zero);

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static PendingEntry Entry(int n) => new($"sha256:{n:D64}", $"post-{n:D6}", n);

    /// <summary>
    /// PIL11. An entry is three members (digest, post id, sequence) and never text; the backlog holds the published number of them, refuses the next and says it
    /// overflowed; and a second add of one digest is one entry, not two.
    /// </summary>
    [Fact]
    public void R11_69_PIL11_TheBacklogHoldsThreeMembersPerEntryTheBoundAndOneEntryPerDigest()
    {
        var bound = PublishedIndexLag.BacklogBound();
        var backlog = new IndexBacklog();

        var members = string.Join(",", typeof(PendingEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal));
        var first = backlog.Add(Entry(1), Due);
        var again = backlog.Add(Entry(1), Due);
        var afterTwoAddsOfOne = backlog.Count;
        var refused = 0;
        for (var n = 2; n <= bound + 1; n++)
            if (!backlog.Add(Entry(n), Due)) refused++;

        var expected = $"members Digest,PostId,Sequence; one digest added twice is 1 entry; {bound} held and 1 refused; Overflowed True";
        var actual = $"members {members}; one digest added twice is {afterTwoAddsOfOne} entry (adds {first}, {again}); {backlog.Count} held and {refused} refused; Overflowed {backlog.Overflowed}";
        var holds = members == "Digest,PostId,Sequence" && afterTwoAddsOfOne == 1 && backlog.Count == bound && refused == 1 && backlog.Overflowed;
        Said(expected, holds ? expected : actual);
    }
}
