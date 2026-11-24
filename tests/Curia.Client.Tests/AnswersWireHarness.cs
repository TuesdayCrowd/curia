using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 9 (errata G76) probes' Forum: <see cref="StubLog"/> behind a <see cref="ProvenanceRewriter"/>, so a probe serves the question, the answer or a revision with the members R9.69 to R9.71 add
/// written exactly as the probe chooses, and reads what the reference reader prints of it. The oracle is the JSON the probe wrote. The stub's own post is a real signed question and verifies,
/// so a frame line a probe asserts is the line of a page that is otherwise honest.
/// </summary>
internal sealed class AnswersWireHarness : IDisposable
{
    private readonly HttpClient _http;

    internal AnswersWireHarness()
    {
        Log = new StubLog { ThreadRouteServesTheThread = true };
        Rewriter = new ProvenanceRewriter(Log.Handler);
        _http = new HttpClient(Rewriter, disposeHandler: false) { BaseAddress = StubLog.Forum };
        Client = new ForumClient(_http, StubLog.Forum);
    }

    internal StubLog Log { get; }

    internal ProvenanceRewriter Rewriter { get; }

    internal ForumClient Client { get; }

    public void Dispose()
    {
        _http.Dispose();
        Rewriter.Dispose();
        Log.Dispose();
    }

    /// <summary>Serves <paramref name="provenanceJson"/> as the member <paramref name="name"/> of the stub question's provenance, and sets its kind where <paramref name="kind"/> is given.</summary>
    internal void Member(string postId, string name, string? json, string? kind = null) =>
        Rewriter.Edits[postId] = post =>
        {
            if (json is not null) post["provenance"]![name] = JsonNode.Parse(json);
            if (kind is not null) post["kind"] = kind;
        };

    /// <summary>Several members at once, each (name, raw JSON), on one post.</summary>
    internal void Members(string postId, string? kind, params (string Name, string Json)[] members) =>
        Rewriter.Edits[postId] = post =>
        {
            foreach (var (name, json) in members) post["provenance"]![name] = JsonNode.Parse(json);
            if (kind is not null) post["kind"] = kind;
        };

    /// <summary>Edits the post the stub holds as <paramref name="postId"/> with <paramref name="edit"/> (the whole post object).</summary>
    internal void Edit(string postId, Action<JsonObject> edit) => Rewriter.Edits[postId] = edit;

    internal async Task<ForumResult<ProvenancePost>> ReadAsync(string postId) =>
        await Client.GetPostAsync(postId, MarkingMode.None, TestContext.Current.CancellationToken);

    /// <summary>The post read and verified as <c>curia read</c> verifies it.</summary>
    internal async Task<Passage> PassageAsync(string postId)
    {
        var read = await ReadAsync(postId);
        Assert.True(read.TryGetValue(out var post, out var refusal), refusal?.Summary);
        var (passages, _) = await Program.VerifyAllAsync(Client, [post!], TestContext.Current.CancellationToken);
        return passages[0];
    }

    /// <summary>The frame of a rendered passage: the lines before the first blank one.</summary>
    internal static string[] FrameLines(string rendered) =>
        [.. rendered.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').TakeWhile(l => l.Length > 0)];

    /// <summary>The frame lines of the passage that begin with <paramref name="prefix"/>.</summary>
    internal static string[] Lines(string rendered, string prefix) =>
        [.. FrameLines(rendered).Where(l => l.StartsWith(prefix, StringComparison.Ordinal))];

    /// <summary>The titled index row <c>curia search --titles</c> and <c>board --titles</c> write for a passage, as the lines it wrote.</summary>
    internal static string[] IndexRows(Passage passage)
    {
        var frame = new FrameBuilder();
        TitledIndex.Write(frame, 1, passage, MarkingMode.None, null);
        return [.. frame.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
    }

    internal static ImmutableArray<Successor> Revision(string digest, string? postId) => [new Successor(digest, postId)];
}
