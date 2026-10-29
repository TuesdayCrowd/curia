using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G47, R10.87), probes CHN16 to CHN18: the line a reader prints for an accepted post of kind <c>revision</c> says it is the text accepted for
/// the answer it revises, by the asker of that answer's thread, and never calls the thread by the revision's own <c>parent</c>.
///
/// <para><b>Where the expectation comes from.</b> The statement and the words it must not carry are R10.87's code spans, read from the errata by
/// <see cref="PublishedChainWords"/>; the basis parenthesis every kind's statement carries is held to R10.79's clauses (the words
/// <see cref="AcceptedLineTests"/> reads from the errata). No fact compares a rendering with <see cref="Passage.AcceptedStatement"/>, the constant under
/// change. The revision's <c>parent</c> is a marker string that no honest field would hold, so a statement that printed it as the thread is caught.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RevisionAcceptedLineTests : IDisposable
{
    private const string Marker = "ZQ6MARKERSTRANGERQUESTION";
    private static readonly string[] BasisClauses = ["the Forum's word", "no signature covers an acceptance", "checks nothing"];

    private readonly string _root = Directory.CreateTempSubdirectory("curia-revision-line-").FullName;
    private readonly EnrolledAgent _agent;

    public RevisionAcceptedLineTests()
    {
        Assert.True(new ProfileStore(_root).Create("alice", "https://agents.example/alice", "alice-1", new Uri("http://forum.test")).TryGetValue(out var agent, out _));
        _agent = agent!;
    }

    public void Dispose()
    {
        _agent.Dispose();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A signed post of <paramref name="kind"/> served honestly, with <paramref name="accepted"/> as the Forum's word and <paramref name="parent"/> as the author's.</summary>
    private Passage Make(PostKind kind, bool accepted, string? parent = null)
    {
        var draft = new PostDraft
        {
            Kind = kind,
            Board = "b",
            Title = kind == PostKind.Question ? "A question" : null,
            Body = "A body.",
            Parent = parent,
            Prev = kind == PostKind.Revision ? "sha256:" + new string('1', 64) : null,
        };
        Assert.True(SubmissionBuilder.Build(_agent, draft, DateTimeOffset.UnixEpoch).TryGetValue(out var signed, out var error), error?.Type);

        var canonical = Encoding.UTF8.GetString(signed!.Canonical.Span);
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None) with
        {
            Kind = PostKinds.Wire(kind),
            Parent = parent,
            Signature = signed.Signature,
            Digest = signed.PrefixedDigest,
            Accepted = accepted,
        };
        return new Passage(post, new SignatureVerdict(true, "alice-1", "ok", signed.Digest, signed.PrefixedDigest));
    }

    private static string[] FrameLines(string rendered) =>
        [.. rendered.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').TakeWhile(l => l.Length > 0)];

    private static string ThreadLine(string rendered) =>
        FrameLines(rendered).SingleOrDefault(l => l.StartsWith("thread    ", StringComparison.Ordinal))
        ?? throw new Xunit.Sdk.XunitException("Expected: one line beginning 'thread    ' Actual: none");

    private static void AssertRevisionStatement(string where, string text)
    {
        var statement = PublishedChainWords.RevisionStatement();
        var forbidden = PublishedChainWords.RevisionForbidden();
        var problems = new List<string>();
        if (!text.Contains(statement, StringComparison.Ordinal)) problems.Add($"lacks R10.87's \"{statement}\"");
        if (text.Contains(forbidden, StringComparison.Ordinal)) problems.Add($"says \"{forbidden}\"");
        if (text.Contains(Marker, StringComparison.Ordinal)) problems.Add("prints the revision's parent as the thread");
        foreach (var clause in BasisClauses)
            if (!text.Contains(clause, StringComparison.Ordinal)) problems.Add($"lacks the basis parenthesis' \"{clause}\"");
        Assert.True(problems.Count == 0, $"{where} {string.Join("; ", problems)}. Actual: {text}");
    }

    /// <summary>CHN16. The line for an accepted revision states R10.87's words, the basis parenthesis, and neither the thread's asker nor the revision's parent.</summary>
    [Fact]
    public void R10_87_CHN16_TheLineForAnAcceptedRevisionStatesWhatTheForumEstablished()
    {
        var line = ThreadLine(Make(PostKind.Revision, accepted: true, parent: Marker).Render());

        AssertRevisionStatement("the line form", line["thread    ".Length..]);
    }

    /// <summary>CHN17 (a control). The line for an accepted answer is what it was, and a post not accepted prints none, whatever its kind.</summary>
    [Fact]
    public void R10_87_CHN17_OtherKindsKeepTheirWordsAndAnUnacceptedPostPrintsNone()
    {
        var answer = ThreadLine(Make(PostKind.Answer, accepted: true, parent: "01CHN000000000000000000001").Render());

        Assert.Contains("accepted by the thread's asker", answer, StringComparison.Ordinal);
        Assert.DoesNotContain(PublishedChainWords.RevisionStatement(), answer, StringComparison.Ordinal);
        foreach (var kind in new[] { PostKind.Answer, PostKind.Revision, PostKind.Question })
            Assert.DoesNotContain(FrameLines(Make(kind, accepted: false, parent: kind == PostKind.Question ? null : "01CHN000000000000000000001").Render()), l => l.StartsWith("thread    ", StringComparison.Ordinal));
    }

    /// <summary>
    /// CHN18. Every form states it, from one definition: the line form, the titled index row and the screen's header each print the revision statement
    /// for an accepted revision and none prints the old words. Plain <c>curia search</c> prints pointers only and states no acceptance (M6), so it is not
    /// among the forms; the sentence <see cref="AnswerStanding"/> cuts at the parenthesis keeps the basis whole in each.
    /// </summary>
    [Fact]
    public void R10_87_CHN18_EveryFormStatesItFromOneDefinition()
    {
        var passage = Make(PostKind.Revision, accepted: true, parent: Marker);

        var index = new FrameBuilder();
        TitledIndex.Write(index, 1, passage, MarkingMode.None, null);
        var indexRow = index.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Skip(1).First(l => l.StartsWith("   ", StringComparison.Ordinal) && !l.StartsWith("   parent", StringComparison.Ordinal));
        var header = PassageHeader.Rows(passage, 1, 400).Select(r => r.Text).Single(r => r.StartsWith("forum says ", StringComparison.Ordinal));
        var statement = PublishedChainWords.RevisionStatement();

        // The marker is in the index row's parent line and the header's H1 on purpose (the author's word is printed); the statement itself must not carry it.
        var problems = new List<string>();
        foreach (var (form, text) in new[] { ("the titled index row", indexRow), ("the screen's header", header) })
        {
            if (!text.Contains(statement, StringComparison.Ordinal)) problems.Add($"{form} lacks R10.87's statement");
            if (text.Contains(PublishedChainWords.RevisionForbidden(), StringComparison.Ordinal)) problems.Add($"{form} says the thread's asker");
        }

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Index row: {indexRow} Header: {header}");
        AssertRevisionStatement("the line form", ThreadLine(passage.Render())["thread    ".Length..]);
    }
}
