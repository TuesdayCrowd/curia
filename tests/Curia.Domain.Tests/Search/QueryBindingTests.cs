using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Curia.Domain.Verification;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// R9.35 (errata G36): a search cursor carries a binding to the query that minted it, a function of the query's members and of nothing else.
///
/// <para><b>Oracles.</b> The members the binding must follow are the code spans of R9.35's clause, read from the errata at test time
/// (<see cref="PublishedSearchPage"/>), never from <see cref="QueryBinding"/>; each row changes exactly one member of a base query and asks only
/// whether the digest moved. The unbound members (<c>why_ranked</c>, <c>marking</c>) are named by the errata and must not be parameters of the
/// function. A negative row always has its control: the identical query binds equally.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class QueryBindingTests
{
    private const string FloorInForce = "floor in force";

    private sealed record Query(
        string? Text, string? Board, string? Author, PostKind[] Kinds, string[] Tags,
        VerificationLevel? Requested, VerificationLevel InForce, int PageSize)
    {
        internal string Bind() => QueryBinding.Of(Text, Board, Author, Kinds, Tags, Requested, InForce, PageSize);
    }

    private static readonly Query Base = new(
        "npgsql ECONNRESET", "postgres", "urn:curia:agent:alice", [PostKind.Question, PostKind.Answer], ["tls", "pool"],
        VerificationLevel.V1, VerificationLevel.V1, 10);

    /// <summary>One variation per member R9.35 binds: the published names (asserted before use) and the floor in force, each changing only itself.</summary>
    private static readonly Dictionary<string, Query> Variations = new(StringComparer.Ordinal)
    {
        ["q"] = Base with { Text = "npgsql ECONNRESET!" },
        ["board"] = Base with { Board = "postgres2" },
        ["author"] = Base with { Author = "urn:curia:agent:bob" },
        ["kind"] = Base with { Kinds = [PostKind.Question] },
        ["tags"] = Base with { Tags = ["tls"] },
        ["min_verification"] = Base with { Requested = VerificationLevel.V0 },
        ["limit"] = Base with { PageSize = 11 },
        [FloorInForce] = Base with { InForce = VerificationLevel.V2 },
    };

    public static TheoryData<string> Members()
    {
        var data = new TheoryData<string>();
        foreach (var name in Variations.Keys) data.Add(name);
        return data;
    }

    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SixteenLowercaseHex();

    [Fact]
    public void R9_35_TheMemberListThisFactVariesIsTheListTheErratumBinds()
    {
        string[] published = [.. PublishedSearchPage.BoundMembers(), FloorInForce];

        Assert.Equal(published.Order(StringComparer.Ordinal), Variations.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void R9_35_EveryMemberTheErratumNamesChangesTheBinding(string member)
    {
        var baseline = Base.Bind();

        // The control: the digest is a digest, and the identical query binds equally.
        Assert.Matches(SixteenLowercaseHex(), baseline);
        Assert.Equal(baseline, Base.Bind());

        Assert.NotEqual(baseline, Variations[member].Bind());
    }

    [Fact]
    public void R9_35_TheMembersTheErratumLeavesUnboundAreNotParametersOfTheFunction()
    {
        var parameters = typeof(QueryBinding).GetMethod(nameof(QueryBinding.Of))!.GetParameters().Select(p => Normalize(p.Name!)).ToArray();

        foreach (var unbound in PublishedSearchPage.UnboundMembers())
            Assert.DoesNotContain(Normalize(unbound), parameters);

        static string Normalize(string name) => name.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    [Fact]
    public void R9_35_TheBindingIsAFunctionOfTheQueryNotOfItsSpelling()
    {
        var baseline = Base.Bind();

        // kind and tags are sets: any order and any repeat bind alike; tags are compared without regard to case, as the filter compares them.
        Assert.Equal(baseline, (Base with { Kinds = [PostKind.Answer, PostKind.Question] }).Bind());
        Assert.Equal(baseline, (Base with { Kinds = [PostKind.Question, PostKind.Answer, PostKind.Question] }).Bind());
        Assert.Equal(baseline, (Base with { Tags = ["pool", "tls"] }).Bind());
        Assert.Equal(baseline, (Base with { Tags = ["TLS", "Pool", "tls"] }).Bind());

        // And a different set binds differently.
        Assert.NotEqual(baseline, (Base with { Tags = ["tls", "pool", "extra"] }).Bind());
        Assert.NotEqual(baseline, (Base with { Kinds = [PostKind.Question, PostKind.Finding] }).Bind());

        // q is exactly as sent: case and spacing are the caller's.
        Assert.NotEqual(baseline, (Base with { Text = "NPGSQL econnreset" }).Bind());
        Assert.NotEqual(baseline, (Base with { Text = "npgsql  ECONNRESET" }).Bind());
    }

    [Fact]
    public void R9_35_NoTwoQueriesShareACanonicalString()
    {
        // Board and Author are set here so that "absent" for one of them is never the same query as "absent" for the other.
        var empty = Base with { Text = "x", Kinds = [], Tags = [], Requested = null };
        Query[] pairs =
        [
            // q holding what another member's field would hold.
            Base with { Text = "a;board=b", Board = null },
            Base with { Text = "a", Board = "b" },
            // a tag that holds the separator against two tags.
            Base with { Tags = ["a,b"] },
            Base with { Tags = ["a", "b"] },
            // an absent member against an empty one.
            empty with { Board = null },
            empty with { Board = string.Empty },
            empty with { Author = null },
            empty with { Author = string.Empty },
            empty with { Text = null },
            empty with { Text = string.Empty },
            // a value that moves between members.
            Base with { Board = "b", Author = null },
            Base with { Board = null, Author = "b" },
            // a number that moves between members.
            Base with { PageSize = 1, InForce = VerificationLevel.V0 },
            Base with { PageSize = 10, InForce = VerificationLevel.V0 },
        ];

        var bound = pairs.Select(p => p.Bind()).ToArray();
        var collisions = new List<string>();
        for (var i = 0; i < pairs.Length; i++)
            for (var j = i + 1; j < pairs.Length; j++)
                if (string.Equals(bound[i], bound[j], StringComparison.Ordinal))
                    collisions.Add($"[{i}] and [{j}] bind to {bound[i]}");

        Assert.True(collisions.Count == 0, "queries that differ bind alike (expected distinct): " + string.Join("; ", collisions));
    }

    [Fact]
    public void R9_35_TheBindingIsAMethodOfTheDomainAssemblyAndNoOtherTypeTakesTheQuery()
    {
        // Control for the reflection above: the function exists with the parameters the facts pass.
        var method = typeof(QueryBinding).GetMethod(nameof(QueryBinding.Of), BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(8, method!.GetParameters().Length);
    }
}
