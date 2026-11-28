using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R4.41 (rev.): a key set is published from the read that decided the identity's lifecycle, and an entry or a bound key whose index that read does not hold is a fault of the Forum and never a member
/// left out. The function the route publishes with is held here: it returns the index the read holds, and throws for one it does not, so a lifecycle list cannot silently lose an entry.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeySetIndexFaultTests
{
    [Fact]
    public void R4_41_rev_AnIndexTheReadHoldsIsReturnedAsItIs()
    {
        var indexOf = Jwks.Strictly(id => id == "e1" ? 7L : null);

        Assert.Equal(7L, indexOf("e1"));
    }

    [Fact]
    public void R4_41_rev_AnIndexTheReadDoesNotHoldIsAFaultAndNotAnOmittedMember()
    {
        var indexOf = Jwks.Strictly(id => id == "e1" ? 7L : null);

        _ = Assert.Throws<InvalidOperationException>(() => indexOf("e2"));
    }

    /// <summary>The lifecycle list of a key set built over a function that lacks one entry's index cannot be built: the fault surfaces where the list is made.</summary>
    [Fact]
    public void R4_41_rev_ALifecycleListOverAReadThatLacksOneEntryIsAFault()
    {
        var indexOf = Jwks.Strictly(id => id == "e1" ? 7L : null);
        string[] entries = ["e1", "e2"];

        _ = Assert.Throws<InvalidOperationException>(() => entries.Select(indexOf).ToList());
    }
}
