using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.58 and R11.88 (errata G63), LOB44 (Amendment 2, ruling 26): the three artifacts that say the page size, none derived from another. The route's constants (<see cref="BoardListQueryParameters"/>), the client library's
/// (<see cref="BoardCheck"/>, which both clients read) and the numbers the errata publishes (<see cref="PublishedBoardListWords"/>) are asserted equal, each assertion naming which differs. The Forum does not depend on
/// the client library (CS-7), so this test is the join. A control on the current behaviour; FLOB70 to FLOB72 turn it red.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BoardListLimitConstantsTests
{
    [Fact]
    public void R9_58_LOB44_TheRoutesTheClientsAndTheErratasPageSizesAreThreeArtifactsThatAgree()
    {
        var facts = new (string Label, int Route, int Client, int Published)[]
        {
            ("the largest page", BoardListQueryParameters.MaximumLimit, BoardCheck.MaximumLimit, PublishedBoardListWords.MaximumLimit()),
            ("the default page", BoardListQueryParameters.DefaultLimit, BoardListQueryParameters.DefaultLimit, PublishedBoardListWords.DefaultLimit()),
            ("the boards a refusal names", BoardCheck.NamedBoards, BoardCheck.NamedBoards, PublishedBoardListWords.RefusalCap()),
        };

        var expected = facts.Select(f => $"{f.Label}: {f.Published} in the route, the client and the errata");
        var actual = facts.Select(f => f.Route == f.Published && f.Client == f.Published
            ? $"{f.Label}: {f.Published} in the route, the client and the errata"
            : $"{f.Label}: the route says {f.Route}, the client {f.Client}, the errata {f.Published}");
        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }
}
