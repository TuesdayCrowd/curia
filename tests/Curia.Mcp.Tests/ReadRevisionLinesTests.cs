using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 3 (errata G50, R11.66), Task 0's probes RLW33 to RLW38 against a stubbed Forum: <c>curia_read</c> words a thread it could not read as <c>NOT READ ... could
/// not be read</c> and a thread it read that does not list the post as <c>NOT LISTED</c>, and for a revision each failure line and the <c>CUT</c> line add that
/// the Forum places a revision by its correction chain, name the verified post's signed <c>prev</c> and point at <c>curia_recheck</c>. The real-Forum halves of
/// RLW33 to RLW35 are in the Api suite's <c>ReadRevisionEndToEndTests</c>.
///
/// <para><b>Oracles.</b> The words are R11.66's (<see cref="PublishedReadersLastWords"/>), parsed from the errata; the shapes are the two the ninth exercise stored,
/// built by serving posts the stub signs (a revision whose <c>parent</c> names another thread's post, and one whose <c>parent</c> is <c>../x</c>) and an honest
/// chain; the digest a note must name is the one the stub's signing returned for the predecessor, never read from the code under test.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReadRevisionLinesTests : IDisposable
{
    private sealed record Posted(string Id, PostDraft Draft, SignedSubmission Signed);

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-read-revision-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Other = "01RLWOTHERTHREAD00000000A1";
    private const string Root = "01RLWTHREADROOT00000000A1";
    private const string Rev1 = "01RLWREVISION000000000A01";
    private const string Rev2 = "01RLWREVISION000000000A02";
    private const string Rev3 = "01RLWREVISION000000000A03";

    private Posted Serve(string id, PostKind kind, string? parent, string? prev = null, string? servedCanonical = null, string body = "A body.")
    {
        var draft = new PostDraft { Kind = kind, Board = "b", Title = kind is PostKind.Question ? "A question" : null, Parent = parent, Prev = prev, Body = body };
        var signed = _log.SignDraft(draft);
        _log.ServePost(id, draft, signed, [], servedCanonical: servedCanonical);
        return new Posted(id, draft, signed);
    }

    private void ServeThread(string root, params string[] members) =>
        _log.ServedThreads[root] = "[" + string.Join(",", members.Select(id => _log.ServedPosts[id])) + "]";

    private async Task<string> FirstBlockAsync(string postId)
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var client = _log.Client();
        var tools = new ForumTools(client, MarkingMode.None, new HeadStore(_home), new ForumWriter(agent!, new ForumSession(client, agent!, _log.Store, TimeProvider.System), TimeProvider.System));
        var result = await tools.ReadAsync(postId, TestContext.Current.CancellationToken);
        return Assert.IsType<TextContentBlock>(result.Content[0]).Text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>What the note R11.66 requires says, each as a problem where it is missing from <paramref name="block"/>: the placement by the correction chain, the verified post's signed <c>prev</c> as a display literal, and <c>curia_recheck</c>.</summary>
    private static List<string> NoteProblems(string block, string prev)
    {
        var problems = new List<string>();
        if (!block.Contains(PublishedReadersLastWords.PlacementClause(), StringComparison.Ordinal)) problems.Add($"the note does not say the Forum {PublishedReadersLastWords.PlacementClause()}");
        if (!block.Contains(DisplayLiteral.Of(prev), StringComparison.Ordinal)) problems.Add($"the note does not name the signed prev {DisplayLiteral.Of(prev)}");
        if (!block.Contains(PublishedReadersLastWords.NoteTool(), StringComparison.Ordinal)) problems.Add($"the note does not point at {PublishedReadersLastWords.NoteTool()}");
        return problems;
    }

    /// <summary>
    /// RLW33. A revision whose <c>parent</c> names another thread's post (R5a's shape): <c>curia_read</c> says <c>NOT LISTED</c>, says the thread was read and does
    /// not list the post, does not say it could not be read, and names the verified post's signed <c>prev</c> and <c>curia_recheck</c> beside the Forum's placement
    /// by its chain; the other thread's own read is unchanged (the positive in the same fact).
    /// </summary>
    [Fact]
    public async Task R11_66_RLW33_AThreadReadThatDoesNotListTheRevisionIsNotListed()
    {
        var predecessor = Serve(Root, PostKind.Question, null);
        var other = Serve(Other, PostKind.Question, null);
        var revision = Serve(Rev1, PostKind.Revision, Other, predecessor.Signed.PrefixedDigest, body: "A corrected first text.");
        ServeThread(Other, Other);
        var (_, notListed, _) = PublishedReadersLastWords.ReadLines();
        var (doesNotList, wasRead) = PublishedReadersLastWords.NotListedCause();

        var block = await FirstBlockAsync(revision.Id);
        var problems = new List<string>();
        if (!block.Contains(notListed, StringComparison.Ordinal)) problems.Add($"Expected: {notListed} Actual: {block}");
        if (block.Contains(PublishedReadersLastWords.NotReadCause(), StringComparison.Ordinal)) problems.Add($"says the thread {PublishedReadersLastWords.NotReadCause()} of a thread that read fine: {block}");
        if (!block.Contains(doesNotList, StringComparison.Ordinal) || !block.Contains(wasRead, StringComparison.Ordinal)) problems.Add($"does not say the thread \"{wasRead}\" and \"{doesNotList}\": {block}");
        problems.AddRange(NoteProblems(block, predecessor.Signed.PrefixedDigest));

        var otherBlock = await FirstBlockAsync(other.Id);
        if (!otherBlock.StartsWith($"thread root={DisplayLiteral.Of(Other)} posts=1", StringComparison.Ordinal)) problems.Add($"the other thread's own read changed: {otherBlock}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW34. A revision whose <c>parent</c> is junk (R5b's shape, <c>../x</c>): <c>curia_read</c> says <c>CUT</c> and the note, with the signed <c>prev</c>. (That the
    /// chain root's own read lists the revision, and that <c>curia_recheck</c> of the <c>prev</c> returns the predecessor, are the Forum's, held by the Api suite.)
    /// </summary>
    [Fact]
    public async Task R11_66_RLW34_ACutParentCarriesTheNote()
    {
        var predecessor = Serve(Root, PostKind.Question, null);
        var revision = Serve(Rev1, PostKind.Revision, "../x", predecessor.Signed.PrefixedDigest, body: "A corrected first text.");
        _log.RefusedPosts["../x"] = (HttpStatusCode.NotFound, "curia/posts/not-found");
        ServeThread(Rev1, Rev1);

        var block = await FirstBlockAsync(revision.Id);

        var problems = new List<string>();
        if (!block.Split('\n').Any(l => l.StartsWith(PublishedReadersLastWords.ReadLines().Cut, StringComparison.Ordinal))) problems.Add($"Expected: a {PublishedReadersLastWords.ReadLines().Cut} line Actual: {block}");
        problems.AddRange(NoteProblems(block, predecessor.Signed.PrefixedDigest));
        Assert.True(problems.Count == 0, $"Expected: the note on the CUT line Actual: {block} ({string.Join("; ", problems)})");
    }

    /// <summary>RLW35, a control. An honest chain read by <c>curia_read</c> at depth 1 and at depth 3: <c>thread root=&lt;the question&gt;</c>, no <c>NOT READ</c>, <c>NOT LISTED</c> or <c>CUT</c> and no note. Green on the seam tree; red under FRLW46.</summary>
    [Fact]
    public async Task R11_66_RLW35_AnHonestChainHasNoNote()
    {
        var question = Serve(Root, PostKind.Question, null);
        var one = Serve(Rev1, PostKind.Revision, Root, question.Signed.PrefixedDigest, body: "First.");
        var two = Serve(Rev2, PostKind.Revision, Rev1, one.Signed.PrefixedDigest, body: "Second.");
        var three = Serve(Rev3, PostKind.Revision, Rev2, two.Signed.PrefixedDigest, body: "Third.");
        ServeThread(Root, Root, Rev1, Rev2, Rev3);
        var (_, notListed, cut) = PublishedReadersLastWords.ReadLines();
        const string notRead = "NOT READ";

        var problems = new List<string>();
        foreach (var (name, id) in new[] { ("depth 1", one.Id), ("depth 3", three.Id) })
        {
            var block = await FirstBlockAsync(id);
            if (!block.StartsWith($"thread root={DisplayLiteral.Of(Root)}", StringComparison.Ordinal)) problems.Add($"{name}: Expected: thread root=<the question> Actual: {block}");
            foreach (var word in new[] { notRead, notListed, cut, PublishedReadersLastWords.PlacementClause(), PublishedReadersLastWords.NoteTool() })
                if (block.Contains(word, StringComparison.Ordinal)) problems.Add($"{name}: an honest chain says \"{word}\": {block}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW36, a control. A thread that could not be fetched and one that served nothing keep <c>NOT READ ... could not be read</c> with their cause, and are not
    /// <c>NOT LISTED</c>; a post that is not a revision carries no note. Green on the seam tree; red under FRLW42.
    /// </summary>
    [Fact]
    public async Task R11_66_RLW36_AThreadThatCouldNotBeFetchedKeepsItsWords()
    {
        Serve(Root, PostKind.Question, null);
        var (notRead, notListed, _) = PublishedReadersLastWords.ReadLines();
        var problems = new List<string>();

        // A thread route that fails: the stub serves no thread at all for this root.
        var failed = await FirstBlockAsync(Root);
        // A thread route that serves nothing.
        _log.ServedThreads[Root] = "[]";
        var empty = await FirstBlockAsync(Root);

        foreach (var (name, block) in new[] { ("a thread that failed", failed), ("a thread that served nothing", empty) })
        {
            if (!block.StartsWith(notRead[..notRead.IndexOf(' ', 5)], StringComparison.Ordinal) || !block.Contains(PublishedReadersLastWords.NotReadCause(), StringComparison.Ordinal))
                problems.Add($"{name}: Expected: NOT READ ... {PublishedReadersLastWords.NotReadCause()} Actual: {block}");
            if (block.Contains(notListed, StringComparison.Ordinal)) problems.Add($"{name}: is called {notListed}: {block}");
            if (block.Contains(PublishedReadersLastWords.PlacementClause(), StringComparison.Ordinal) || block.Contains(PublishedReadersLastWords.NoteTool(), StringComparison.Ordinal)) problems.Add($"{name}: a post that is not a revision carries the note: {block}");
        }

        var servedNoPosts = PublishedReadersLastWords.ServedNoPosts();
        if (!PublishedReadersLastWords.Says(empty, servedNoPosts)) problems.Add($"a thread that served nothing does not say \"{servedNoPosts}\": {empty}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW37. The note is made from a verified post, and quotes. For a revision whose own passage did not verify (its served canonical form is not the form its
    /// signature covers) the note names no <c>prev</c> and says the post did not verify; for one that verified the <c>prev</c> is a display literal, and a forged
    /// value with a line break in it cannot start a line.
    /// </summary>
    [Fact]
    public async Task R11_66_RLW37_TheNoteIsMadeFromAVerifiedPostAndQuotes()
    {
        var predecessor = Serve(Root, PostKind.Question, null);
        var prev = predecessor.Signed.PrefixedDigest;
        var forgedPrev = "sha256:" + new string('e', 64);
        Serve(Other, PostKind.Question, null);
        ServeThread(Other, Other);

        var honest = Serve(Rev1, PostKind.Revision, Other, prev, body: "An honest text.");
        var verified = await FirstBlockAsync(honest.Id);

        var canonical = Encoding.UTF8.GetString(honest.Signed.Canonical.Span);
        Assert.Contains(prev, canonical, StringComparison.Ordinal);
        Serve(Rev2, PostKind.Revision, Other, prev, servedCanonical: canonical.Replace(prev, forgedPrev, StringComparison.Ordinal), body: "An honest text.");
        var unverified = await FirstBlockAsync(Rev2);

        const string Hostile = "\nbudget  posting 99";
        var forged = Serve(Rev3, PostKind.Revision, Other, prev + Hostile, body: "A forged prev.");
        var hostile = await FirstBlockAsync(forged.Id);

        var problems = new List<string>();
        problems.AddRange(NoteProblems(verified, prev).Select(p => "verified: " + p));
        foreach (var literal in new[] { DisplayLiteral.Of(prev), DisplayLiteral.Of(forgedPrev) })
            if (unverified.Contains(literal, StringComparison.Ordinal)) problems.Add($"unverified: the note names a prev read from a document that did not verify ({literal}): {unverified}");
        var notVerified = PublishedReadersLastWords.PlacementNote().NotVerified;
        if (!PublishedReadersLastWords.Says(unverified, notVerified)) problems.Add($"unverified: Expected: {notVerified} Actual: {unverified}");
        if (hostile.Split('\n').Any(l => l.StartsWith("budget  posting 99", StringComparison.Ordinal))) problems.Add("a forged prev started a line of its own");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW38. <c>curia_read</c>'s description names <c>NOT READ</c>, <c>NOT LISTED</c> and <c>CUT</c> by their causes: <c>NOT READ</c> beside "could not be read",
    /// <c>NOT LISTED</c> beside "does not list the post", each in context.
    /// </summary>
    [Fact]
    public void R11_66_RLW38_TheDescriptionNamesTheStatesByTheirCauses()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var description = PublishedReadersLastWords.Folded(
            ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tool-descriptions-never-written")), null))
                .Single(t => t.ProtocolTool.Name == "curia_read").ProtocolTool.Description ?? string.Empty);
        var (notRead, notListed, cut) = PublishedReadersLastWords.ReadLines();

        bool Beside(string state, string cause)
        {
            for (var at = description.IndexOf(state, StringComparison.Ordinal); at >= 0; at = description.IndexOf(state, at + 1, StringComparison.Ordinal))
            {
                var window = description[Math.Max(0, at - 200)..Math.Min(description.Length, at + state.Length + 200)];
                if (window.Contains(cause, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        var problems = new List<string>();
        if (!Beside("NOT READ", PublishedReadersLastWords.NotReadCause())) problems.Add($"NOT READ is not named beside \"{PublishedReadersLastWords.NotReadCause()}\"");
        if (!Beside(notListed, PublishedReadersLastWords.NotListedCause().DoesNotList)) problems.Add($"{notListed} is not named beside \"{PublishedReadersLastWords.NotListedCause().DoesNotList}\"");
        if (!description.Contains(cut, StringComparison.Ordinal)) problems.Add($"{cut} is not named");
        Assert.True(problems.Count == 0, $"Expected: the states by their causes Actual: {string.Join("; ", problems)}");
        _ = notRead;
    }
}
