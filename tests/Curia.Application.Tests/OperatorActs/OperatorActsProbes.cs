using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Application.Credentials;
using Curia.Application.Ingest;
using Curia.Application.Projections;
using Curia.Application.Tests.Credentials;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Xunit;

namespace Curia.Application.Tests.OperatorActs;

/// <summary>
/// PR N2 (errata G70, R6.66, R6.67), Task 1: the Application probes that build today. OAS9 and OAS32 are the parse halves (the comparison with <c>OperatorActPredicates.All</c> and with the words the tool prints
/// is pending, in the <c>.pending</c> files, until the types exist); OAS15's forged-delta leg asks <c>LogFacts.ServableAfter</c> for a delta that holds a forged withhold. (OAS10 is the edited LRV33, in
/// <c>PublishedKeysTests</c>: red on the tree, two classes.)
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActsProbes
{
    private static string Errata()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
    }

    /// <summary>The requirement's text: from <paramref name="id"/> in bold to the next requirement or heading (R6.67's seven steps are a list after its first paragraph).</summary>
    private static string Paragraph(string id)
    {
        var text = Errata();
        var at = text.IndexOf($"**{id}** ", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {id} paragraph in the errata");
        var next = Regex.Match(text[(at + 2)..], @"\n(\*\*R\d|#{1,3} )");
        Assert.True(next.Success, $"{id} is not followed by another requirement or a heading");
        return text[at..(at + 2 + next.Index)];
    }

    /// <summary>OAS9, parse half: R6.67's paragraph names eight predicate slugs, by the names the procedure publishes; green once the entry is pasted. The comparison with <c>OperatorActPredicates.All</c> is pending.</summary>
    [Fact]
    public void OAS9_TheErrataNamesTheEightPredicates()
    {
        var slugs = Regex.Matches(Paragraph("R6.67"), @"`(act-[a-z-]+)`").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["act-statement-mismatch", "act-unsigned", "act-unknown-kind", "act-broken-link", "act-typ-mismatch", "act-key-unpublished", "act-key-not-first", "act-signature-invalid"],
            slugs);
    }

    /// <summary>OAS9, comparison half: <c>OperatorActPredicates.All</c> is R6.67's eight slugs, in the paragraph's order, parsed from the errata and not from the type.</summary>
    [Fact]
    public void OAS9_TheTypeCarriesTheErratasEightPredicatesInOrder()
    {
        var slugs = Regex.Matches(Paragraph("R6.67"), @"`(act-[a-z-]+)`").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(8, slugs.Length);
        Assert.Equal(slugs, OperatorActPredicates.All.ToArray());
    }

    /// <summary>
    /// OAS9b: the header <c>typ</c> of an operator act is written in two places in the code, the third row of <c>PublishedKeys.Classes</c> and the fold's step 5, and nothing ties them but this test: each is
    /// compared with the <c>typ</c> parsed out of R6.66's paragraph in the errata, a third artifact (Amendment 3, ruling 43).
    /// </summary>
    [Fact]
    public void OAS9b_BothHeaderTypLiteralsAreTheOneTheErrataStates()
    {
        var typ = Regex.Match(Paragraph("R6.66"), @"header `typ` `([^`]+)`");
        Assert.True(typ.Success, "R6.66 does not state a header `typ`");
        var stated = typ.Groups[1].Value;
        Assert.Equal(stated, PublishedKeys.Classes.Single(c => c.Class == LogEntries.OperatorKeyType).HeaderTyp);
        Assert.Equal(OperatorActFold.HeaderTyp, stated);
    }

    /// <summary>OAS32, parse half: R11.97 states the problem type of a row that is not honoured and the two exit codes; green once the entry is pasted. The comparison with the words the tool prints is pending.</summary>
    [Fact]
    public void OAS32_TheErrataStatesTheWordsOfR1197()
    {
        var paragraph = Paragraph("R11.97");
        Assert.Contains("`curia/acts/not-honoured`", paragraph, StringComparison.Ordinal);
        Assert.Contains("exit 7", paragraph, StringComparison.Ordinal);
        Assert.Contains("exit 3", paragraph, StringComparison.Ordinal);
        Assert.Contains("EventId 7010", paragraph, StringComparison.Ordinal);
    }

    private static AppendedEvent ModerationEvent(long index, string post, string effect) =>
        LogEvents.At(index, post, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), $"act-{index}", "moderation.applied", "operator:ops",
            new JsonValue.Object(
            [
                new("actor_id", new JsonValue.String("operator:ops")),
                new("adjudicates", new JsonValue.Array([])),
                new("category", new JsonValue.String("spam")),
                new("digest", new JsonValue.String("sha256:" + new string('1', 64))),
                new("effect", new JsonValue.String(effect)),
                new("moderator", new JsonValue.String("human")),
                new("post_id", new JsonValue.String(post)),
                new("rationale", new JsonValue.String("forged by a probe")),
            ]));

    /// <summary>OAS15, the forged-delta leg: <c>ServableAfter</c> over a delta that holds a forged (unsigned) withhold says the post is servable; the control, a delta with no act, says servable too. Red on the tree: not servable.</summary>
    [Fact]
    public void OAS15_AForgedWithholdInTheDeltaDoesNotMakeAPostUnservable()
    {
        const string post = "01K4CQ4B0000000000000000P1";
        var facts = LogFacts.OfHonoured(SignedActWriters.Verifier.OfLog([]));
        var control = facts.ServableAfter([])(post);
        var forged = facts.ServableAfter([ModerationEvent(0, post, "withhold")])(post);
        Assert.True(control, "the control, a delta with no act, must say servable");
        Assert.True(forged, "Expected: servable  Actual: not servable");
    }
}
