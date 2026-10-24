using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G28, R10.79: a reader that prints a post states, in its own frame and never inside the span, that the Forum serves
/// it as the accepted answer of its thread, as the Forum's word; it states nothing for a post the Forum does not serve as
/// accepted. The words are one statement shared by the line form and the screen's header (R10.74).
///
/// <para><b>Where the expectation comes from.</b> Not from <see cref="Passage"/> or the screen: the clauses the statement must
/// carry are those of R10.79's sentence, parsed from the errata at test time (the test fails if the errata stops holding them),
/// and the words it must not use are the plan's list for R10.50's no-badge rule.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptedLineTests : IDisposable
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>R10.79's three clauses, as the words of its sentence: whose word, what no signature covers, whose choice and what it checks.</summary>
    private static readonly string[] Clauses = ["the Forum's word", "no signature covers an acceptance", "asker", "checks nothing"];

    /// <summary>R10.50: no field is a badge, so none of these may stand in the statement.</summary>
    private static readonly string[] Badges = ["verified", "correct", "trusted", "endorsed", "true"];

    private readonly string _root = Directory.CreateTempSubdirectory("curia-accepted-line-").FullName;
    private readonly EnrolledAgent _agent;

    public AcceptedLineTests()
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

    /// <summary>The paragraph of the errata that defines R10.79, and a check that it holds the clauses the statement is held to.</summary>
    private static string R1079()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata))) dir = dir.Parent;
        Assert.NotNull(dir);

        var text = File.ReadAllText(Path.Combine(dir!.FullName, Errata));
        var match = Regex.Match(text, @"\*\*R10\.79\*\*(?<p>.*?)\n\n", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        Assert.True(match.Success, "the errata no longer carries R10.79");
        var sentence = match.Groups["p"].Value.Replace("\n", " ", StringComparison.Ordinal);
        foreach (var clause in Clauses)
            Assert.True(sentence.Contains(clause, StringComparison.Ordinal), $"R10.79 no longer holds the words '{clause}'");
        Assert.Contains("badge", sentence, StringComparison.Ordinal);
        return sentence;
    }

    /// <summary>A signed question titled <paramref name="title"/>, served honestly, with <paramref name="accepted"/> as the Forum's word.</summary>
    private Passage Passage(string title, string body, bool accepted)
    {
        var draft = new PostDraft { Kind = PostKind.Question, Board = "b", Title = title, Body = body };
        Assert.True(SubmissionBuilder.Build(_agent, draft, DateTimeOffset.UnixEpoch).TryGetValue(out var signed, out var error), error?.Type);

        var canonical = Encoding.UTF8.GetString(signed!.Canonical.Span);
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None) with
        {
            Signature = signed.Signature,
            Digest = signed.PrefixedDigest,
            Accepted = accepted,
        };

        return new Passage(post, new SignatureVerdict(true, "alice-1", "ok", signed.Digest, signed.PrefixedDigest));
    }

    /// <summary>The frame lines of a rendering: those before the first blank line (the span and the standing sentences follow it).</summary>
    private static string[] FrameLines(string rendered) =>
        [.. rendered.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').TakeWhile(l => l.Length > 0)];

    [Fact]
    public void R10_79_APassageOfAnAcceptedPostStatesItInTheFrameAndNotInTheSpan()
    {
        R1079();
        var accepted = Passage("A question", "A body.", accepted: true).Render();
        var plain = Passage("A question", "A body.", accepted: false).Render();

        var frame = FrameLines(accepted);
        var lines = frame.Where(l => l.StartsWith("thread    ", StringComparison.Ordinal)).ToArray();
        Assert.True(lines.Length == 1, $"Passage.Render printed {lines.Length} line(s) beginning 'thread    ' for a post served accepted:true; the frame:\n{string.Join('\n', frame)}");

        foreach (var clause in Clauses)
            Assert.True(lines[0].Contains(clause, StringComparison.Ordinal), $"the statement lacks R10.79's '{clause}': {lines[0]}");
        foreach (var badge in Badges)
            Assert.DoesNotContain(badge, lines[0], StringComparison.OrdinalIgnoreCase);

        // Outside the span: before the span's opening delimiter.
        Assert.True(accepted.IndexOf(lines[0], StringComparison.Ordinal) < accepted.IndexOf(Datamarking.OpenDelimiter, StringComparison.Ordinal));

        // Control: a post the Forum does not serve as accepted has no such line.
        Assert.DoesNotContain(FrameLines(plain), l => l.StartsWith("thread    ", StringComparison.Ordinal));
    }

    [Fact]
    public void R10_79_ATitledIndexRowStatesAnAcceptedPost()
    {
        R1079();
        var accepted = new FrameBuilder();
        TitledIndex.Write(accepted, 1, Passage("A question", "A body.", accepted: true), MarkingMode.None, null);
        var plain = new FrameBuilder();
        TitledIndex.Write(plain, 1, Passage("A question", "A body.", accepted: false), MarkingMode.None, null);

        var rows = accepted.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.StartsWith("#1 post ", rows[0], StringComparison.Ordinal);
        Assert.True(
            rows[1].StartsWith("   ", StringComparison.Ordinal) && Clauses.All(c => rows[1].Contains(c, StringComparison.Ordinal)),
            $"the index row for an accepted post states no acceptance beneath its first line:\n{accepted}");

        foreach (var clause in Clauses)
            Assert.DoesNotContain(clause, plain.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void R10_79_TheScreenAndTheLineFormShareOneAcceptanceStatement()
    {
        R1079();
        var passage = Passage("A question", "A body.", accepted: true);

        var line = FrameLines(passage.Render()).SingleOrDefault(l => l.StartsWith("thread    ", StringComparison.Ordinal));
        Assert.True(line is not null, "the line form states no acceptance, so the screen's header has no statement to share with it");
        var statement = line!["thread    ".Length..];

        var h6 = PassageHeader.Rows(passage, 1, 400).Select(r => r.Text).Single(r => r.StartsWith("forum says ", StringComparison.Ordinal));
        Assert.True(h6.Contains(statement, StringComparison.Ordinal), $"H6 does not carry the line form's statement.\nstatement: {statement}\nH6: {h6}");
        Assert.Contains("no signature covers an acceptance", statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The statement is the reader's own line: an author who writes the same words cannot move it into the span, and a post
    /// the Forum does not serve as accepted gets no line whatever its body says. Green first (held by FX3).
    /// </summary>
    [Fact]
    public void R10_79_NoAcceptanceStatementIsAnyPostsContent()
    {
        R1079();
        const string Mimic = "accepted by the thread's asker (the Forum's word: no signature covers an acceptance, and it checks nothing about the answer)";

        var unaccepted = Passage("A question", Mimic, accepted: false).Render();
        Assert.Contains("no signature covers an acceptance", unaccepted, StringComparison.Ordinal);
        Assert.DoesNotContain(FrameLines(unaccepted), l => l.StartsWith("thread    ", StringComparison.Ordinal));

        var accepted = Passage("A question", Mimic, accepted: true);
        var rendered = accepted.Render();
        var open = rendered.IndexOf(Datamarking.OpenDelimiter, StringComparison.Ordinal);
        Assert.True(open > 0);
        Assert.Contains(accepted.Post.Rendered, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(rendered[open..].Split('\n'), l => l.StartsWith("thread    ", StringComparison.Ordinal));
    }
}
