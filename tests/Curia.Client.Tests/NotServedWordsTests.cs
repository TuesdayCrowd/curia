using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G50, R10.91), Task 0's probes RLW9, RLW10, RLW11, RLW13 and RLW43 at the reference client: a post the Forum lists by its digest alone and does
/// not serve is said <c>not served; recheck of its digest says whether it is withheld</c> by every form that prints such an entry, from one definition.
/// <b>CONSISTENCY CHECK (RLW42): this file calls <c>Supersession.</c> and <c>AnswerStanding.</c> on purpose</b>, to drive each form; every word it expects is
/// parsed from G50 (<see cref="PublishedReadersLastWords"/>), and a form's own output is never its expectation.
///
/// <para><b>RLW9 is an inventory by what a form takes, not by a literal</b> (finding 12, ruling 19): every public method of <c>Supersession</c>,
/// <c>AnswerStanding</c> and <c>CitationLines</c> that takes a successor list, a <c>ProvenancePost</c> (or an array of them) or an
/// <c>AcceptedAnswerMember</c> is in a table of builders or is a reasoned row, so a form added later (or one that, like <c>TextThatStands</c>, takes a root and
/// its members) is red until it is listed. RLW14 is the literal scan, which cannot see a form that omits the words.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class NotServedWordsTests : IDisposable
{
    private static readonly string D1 = "sha256:" + new string('1', 64);
    private static readonly string D2 = "sha256:" + new string('2', 64);
    private const string R1 = "01REVISIONID00000000000001";
    private const string R2 = "01OTHERTHREAD0000000000001";
    private const string RootId = "01ROOTQUESTION000000000001";

    private readonly string _root = Directory.CreateTempSubdirectory("curia-not-served-").FullName;
    private readonly EnrolledAgent _agent;

    public NotServedWordsTests()
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

    // ----- builders -----

    private static ImmutableArray<Successor> Listed(bool digestOnly) => [new Successor(D1, digestOnly ? null : R1)];

    private static ProvenancePost Post(string id, string digest, ImmutableArray<Successor> supersededBy, AcceptedAnswerMember? accepted = null, string kind = "question")
    {
        var post = SpanCheckTests.Post("{}", "{}", MarkingMode.None);
        return post with
        {
            PostId = id,
            Digest = digest,
            Kind = kind,
            Provenance = post.Provenance with { SupersededBy = supersededBy, AcceptedAnswer = accepted },
        };
    }

    /// <summary>A root listing <paramref name="listed"/> and the members the thread holds: the root alone, or, when asked, the successor's own post (a head).</summary>
    private static (ProvenancePost Root, ImmutableArray<ProvenancePost> Members) Thread(ImmutableArray<Successor> listed, bool holdsTheHead)
    {
        var root = Post(RootId, "sha256:" + new string('0', 64), listed);
        ImmutableArray<ProvenancePost> members = holdsTheHead && listed[0].PostId is { } id ? [root, Post(id, listed[0].Digest, [], kind: "revision")] : [root];
        return (root, members);
    }

    private static string RootLine(bool digestOnly)
    {
        var (root, members) = Thread(Listed(digestOnly), holdsTheHead: !digestOnly);
        return Supersession.RootLine(root, members)?.Text ?? string.Empty;
    }

    private static string TextThatStands(bool digestOnly)
    {
        var (root, members) = Thread(Listed(digestOnly), holdsTheHead: !digestOnly);
        return Supersession.TextThatStands(root, members).Text;
    }

    private static AcceptedAnswerMember Accepted(bool digestOnly) => new(D1, digestOnly ? null : R1, [], "answer");

    /// <summary>
    /// One row per form: the method's public name and how to make its output for an entry the Forum lists by digest alone (<c>true</c>) and for one it serves
    /// (<c>false</c>). A method that takes a successor list, a post or an accepted-answer member and is in neither this table nor
    /// <see cref="ReasonedRows"/> fails <see cref="R10_91_RLW9_EveryFormCarriesTheWords"/>.
    /// </summary>
    private static readonly Dictionary<string, Func<bool, string>[]> Forms = new(StringComparer.Ordinal)
    {
        ["Supersession.Revisions"] = [d => Supersession.Revisions(Listed(d)).Text],
        ["Supersession.Statement"] = [d => Supersession.Statement(Listed(d)).Text],
        ["Supersession.Row"] = [d => Supersession.Row(Listed(d))?.Text ?? string.Empty],
        ["Supersession.ForkBranch"] = [d => Supersession.ForkBranch(Listed(d)).Text],
        ["Supersession.QuestionStanding"] = [d => Supersession.QuestionStanding(Listed(d)).Text],
        ["Supersession.RevisedQuestionPointer"] = [d => Supersession.RevisedQuestionPointer(RootId, Listed(d), []).Text, d => Supersession.RevisedQuestionPointer(RootId, Listed(false), Listed(d)).Text],
        ["Supersession.VerificationLine"] = [d => Supersession.VerificationLine(Listed(d)).Text],
        ["Supersession.PassageLine"] = [d => Supersession.PassageLine(Listed(d))?.Text ?? string.Empty],
        ["Supersession.ReceiptLine"] = [d => Supersession.ReceiptLine(Listed(d)).Text],
        ["Supersession.RootLine"] = [RootLine],
        ["Supersession.TextThatStands"] = [TextThatStands],
        ["AnswerStanding.PassageLine"] = [d => AnswerStanding.PassageLine("question", Accepted(d))?.Text ?? string.Empty, d => AnswerStanding.PassageLine("question", new AcceptedAnswerMember(D2, R2, Listed(d), "answer"))?.Text ?? string.Empty],
        ["AnswerStanding.Row"] = [d => AnswerStanding.Row("question", Accepted(d))?.Text ?? string.Empty, d => AnswerStanding.Row("question", new AcceptedAnswerMember(D2, R2, Listed(d), "answer"))?.Text ?? string.Empty],
        ["AnswerStanding.RevisionStatement"] = [d => AnswerStanding.RevisionStatement("post " + DisplayLiteral.Of(R2), Listed(d)).Text],
    };

    /// <summary>
    /// The public methods that take such an input and are held to the words by another fact, each with its reason: a predicate that prints nothing, the refusal
    /// <c>curia_revise</c> gives (a refusal's own arm: RLW14's literal scan holds it to the one constant), and <c>recheck</c>'s batch (R10.89's clause, RLW10).
    /// </summary>
    private static readonly (string Method, string Reason)[] ReasonedRows =
    [
        ("Supersession.IsSuperseded", "a predicate; it prints nothing"),
        ("Supersession.RevisionRefusal", "a refusal's own arm, which says there is no post id to revise; RLW14 holds its `not served` to the one constant"),
    ];

    private static bool TakesAListedEntry(MethodInfo method) =>
        method.GetParameters().Any(p => p.ParameterType == typeof(ImmutableArray<Successor>)
            || p.ParameterType == typeof(ProvenancePost) || p.ParameterType == typeof(ImmutableArray<ProvenancePost>)
            || p.ParameterType == typeof(AcceptedAnswerMember));

    /// <summary>True when every mention of <paramref name="digest"/>'s literal in <paramref name="text"/> is followed, within eighty characters, by <paramref name="words"/> (so the words are beside the digest, and a digest named twice says them twice).</summary>
    private static bool EveryMentionHasTheWordsBeside(string text, string digest, string words)
    {
        var literal = DisplayLiteral.Of(digest);
        var at = text.IndexOf(literal, StringComparison.Ordinal);
        if (at < 0) return false;
        while (at >= 0)
        {
            var end = at + literal.Length;
            var window = text[end..Math.Min(text.Length, end + 80 + words.Length)];
            var found = window.IndexOf(words, StringComparison.Ordinal);
            if (found < 0 || found > 80) return false;
            at = text.IndexOf(literal, end, StringComparison.Ordinal);
        }

        return true;
    }

    /// <summary>
    /// RLW9. Every form carries the words. The inventory is by what a form takes (reflection over the public methods of <c>Supersession</c>,
    /// <c>AnswerStanding</c> and <c>CitationLines</c>); each listed form states R10.91's definition beside the digest-only entry, however many times it names
    /// it, and states neither the definition nor <c>not served</c> for the served one; a method in no row is red.
    /// </summary>
    [Fact]
    public void R10_91_RLW9_EveryFormCarriesTheWords()
    {
        var words = PublishedReadersLastWords.DefinitionSentence();
        var methods = new[] { typeof(Supersession), typeof(AnswerStanding), typeof(CitationLines) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(TakesAListedEntry).Select(m => $"{t.Name}.{m.Name}"))
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(methods.Length >= 12, $"the reflection found only {methods.Length} forms; the filter is looking in the wrong place");

        var problems = new List<string>();
        foreach (var method in methods.Where(m => !Forms.ContainsKey(m) && ReasonedRows.All(r => r.Method != m)))
            problems.Add($"{method} takes an entry the Forum may list by digest alone and is in neither the table of forms nor a reasoned row");

        foreach (var (name, builders) in Forms)
        {
            for (var i = 0; i < builders.Length; i++)
            {
                var digestOnly = builders[i](true);
                var served = builders[i](false);
                if (!digestOnly.Contains(words, StringComparison.Ordinal)) problems.Add($"{name}[{i}]: Expected: \"{words}\" Actual: {Excerpt(digestOnly)}");
                else if (!EveryMentionHasTheWordsBeside(digestOnly, D1, words)) problems.Add($"{name}[{i}]: a mention of the digest has no words beside it: {Excerpt(digestOnly)}");
                if (served.Contains(words, StringComparison.Ordinal) || served.Contains("not served", StringComparison.Ordinal)) problems.Add($"{name}[{i}]: says the served entry is not served: {Excerpt(served)}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string Excerpt(string text) => text.Length <= 220 ? text : text[..220] + "...";

    /// <summary>
    /// RLW10. <c>recheck</c> says it once: the <c>superseded</c> line for a digest-only successor holds R10.89's clause exactly once beside the bare
    /// <c>(not served)</c> of the list, and the shared form beside it carries the pointer, so a pointer added to the shared words alone would print twice.
    /// </summary>
    [Fact]
    public void R10_91_RLW10_RecheckSaysThePointerOnce()
    {
        var pointer = PublishedReadersLastWords.PointerTailOfThatDigest();
        var item = new CitationDocument(D1, "superseded", [D1], false, null);
        var report = CitationLines.For([D1], [item]);
        Assert.Null(report.Error);
        var line = report.Lines.Single().Text.Text;

        var problems = new List<string>();
        var count = Count(line, pointer);
        if (count != 1) problems.Add($"Expected: R10.89's clause (\"{pointer}\") once Actual: {count} times in {Excerpt(line)}");
        if (!line.Contains("(not served)", StringComparison.Ordinal)) problems.Add($"the list's bare (not served) is gone: {Excerpt(line)}");
        var shared = Supersession.Revisions(Listed(true)).Text;
        if (!shared.Contains(PublishedReadersLastWords.PointerTail(), StringComparison.Ordinal)) problems.Add($"Expected: the shared form carries the pointer Actual: {shared}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// RLW11. The accepted answer, the same words: the passage's <c>answers</c> line and the titled index's row for a question whose accepted answer the Forum
    /// lists by digest alone each carry the pointer, and neither says <c>withheld</c> of it. (The third site, <c>curia_read</c>'s <c>accepted</c> line, is the
    /// tool's and is held in <c>NotServedToolTests</c>; the three compose the named post differently, so the fact compares the pointer span and not the line.)
    /// </summary>
    [Fact]
    public void R10_91_RLW11_TheAcceptedAnswerSaysTheSameWords()
    {
        var pointer = PublishedReadersLastWords.PointerTail();
        var problems = new List<string>();
        foreach (var (name, text) in new[]
        {
            ("answers line", AnswerStanding.PassageLine("question", Accepted(true))?.Text ?? string.Empty),
            ("answers row", AnswerStanding.Row("question", Accepted(true))?.Text ?? string.Empty),
        })
        {
            if (!text.Contains(pointer, StringComparison.Ordinal)) problems.Add($"{name}: Expected: \"{pointer}\" Actual: {Excerpt(text)}");
            if (text.Contains(PublishedReadersLastWords.ForbiddenClaim(), StringComparison.Ordinal) && !text.Contains(pointer, StringComparison.Ordinal)) problems.Add($"{name}: says withheld from a digest");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- RLW13 -----

    /// <summary>A signed question served honestly, with <paramref name="superseded"/> as the Forum's word.</summary>
    private Passage PassageOf(ImmutableArray<Successor> superseded)
    {
        var draft = new PostDraft { Kind = PostKind.Question, Board = "b", Title = "A question", Body = "A body." };
        Assert.True(SubmissionBuilder.Build(_agent, draft, DateTimeOffset.UnixEpoch).TryGetValue(out var signed, out var error), error?.Type);
        var canonical = Encoding.UTF8.GetString(signed!.Canonical.Span);
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None);
        post = post with { Signature = signed.Signature, Digest = signed.PrefixedDigest, Provenance = post.Provenance with { SupersededBy = superseded } };
        return new Passage(post, new SignatureVerdict(true, "alice-1", "ok", signed.Digest, signed.PrefixedDigest));
    }

    /// <summary>
    /// RLW13 (amended 2026-10-30, M5). The CLI's forms carry the pointer for the same state: the passage <c>curia read</c> and <c>curia thread</c> print, the titled
    /// index <c>curia board --titles</c> and <c>curia search --titles</c> print, the verification <c>curia verify</c> prints, and the screen's header H6 row
    /// (<c>curia browse</c> in a non-terminal prints the verb's own form byte for byte, so it is the same fact). Each states it beside the digest and a served
    /// successor is not called unserved.
    /// </summary>
    [Fact]
    public void R10_91_RLW13_TheCliFormsCarryThePointer()
    {
        var words = PublishedReadersLastWords.DefinitionSentence();
        var forms = new (string Name, Func<ImmutableArray<Successor>, string> Render)[]
        {
            ("curia read, curia thread (the passage)", list => string.Join('\n', PassageOf(list).Render().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').TakeWhile(l => l.Length > 0))),
            ("curia board --titles (the index)", list =>
            {
                var frame = new FrameBuilder();
                TitledIndex.Write(frame, 1, PassageOf(list), MarkingMode.None, null);
                return frame.ToString();
            }),
            ("curia verify (the verification)", list => Supersession.VerificationLine(list).Text),
            ("curia browse (the screen's H6 row)", list => string.Join('\n', PassageHeader.Rows(PassageOf(list), 1, 400).Select(r => r.Text).Where(r => r.StartsWith("forum says ", StringComparison.Ordinal)))),
        };

        var problems = new List<string>();
        foreach (var (name, render) in forms)
        {
            var digestOnly = render(Listed(true));
            var served = render(Listed(false));
            if (!digestOnly.Contains(words, StringComparison.Ordinal)) problems.Add($"{name}: Expected: \"{words}\" Actual: {Excerpt(digestOnly)}");
            else if (!EveryMentionHasTheWordsBeside(digestOnly, D1, words)) problems.Add($"{name}: the words are not beside the digest: {Excerpt(digestOnly)}");
            if (served.Contains("not served", StringComparison.Ordinal)) problems.Add($"{name}: calls a served successor unserved: {Excerpt(served)}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ----- RLW15 (the help's half) -----

    /// <summary>
    /// RLW15 (the help's half; the tools' is <c>NotServedToolTests</c>). Every <c>curia help</c> entry whose text prints a digest-only entry (<c>read</c>,
    /// <c>thread</c>, <c>search</c>, <c>recheck</c> and <c>browse</c> whatever their text says today, and any other verb whose help says <c>not served</c>)
    /// carries R10.91's definition sentence.
    /// </summary>
    [Fact]
    public void R10_91_RLW15_EveryHelpEntryThatPrintsADigestOnlyEntryDefinesIt()
    {
        var verbs = System.Text.RegularExpressions.Regex.Matches(Help.Usage, @"\bcuria ([a-z]+)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        string[] printers = ["read", "thread", "search", "recheck", "browse"];
        Assert.True(verbs.Length >= 15, $"only {verbs.Length} verbs were read from the usage text");

        var sentence = PublishedReadersLastWords.DefinitionSentence();
        var problems = new List<string>();
        foreach (var verb in verbs.Concat(printers).Distinct(StringComparer.Ordinal))
        {
            var entry = Help.For(verb);
            if (entry is null) { if (printers.Contains(verb)) problems.Add($"{verb}: no help entry"); continue; }
            if (!printers.Contains(verb) && !entry.Contains("not served", StringComparison.Ordinal)) continue;
            if (!PublishedReadersLastWords.Folded(entry).Contains(sentence, StringComparison.Ordinal)) problems.Add($"{verb}: Expected: the definition \"{sentence}\" in its help");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ----- RLW39 (the help's half) and RLW32 (the help's half) -----

    /// <summary>RLW39 (the help's half; the description's is in the adapter's <c>ReadersLastWordsPinsTests</c>). <c>curia help verify</c> carries R11.68's three clauses.</summary>
    [Fact]
    public void R11_68_RLW39_TheHelpOfVerifyAdvisesTheHostThatBatches()
    {
        var help = PublishedReadersLastWords.Folded(Help.For("verify") ?? string.Empty);
        var missing = PublishedReadersLastWords.BatchClauses().Where(c => !help.Contains(PublishedReadersLastWords.Folded(c), StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"Expected: `curia help verify` to carry {string.Join(" and ", missing.Select(m => $"\"{m}\""))}");
    }

    /// <summary>RLW32 (the help's half; the description's is in the adapter's <c>QuestionChainToolTests</c>). <c>curia help answer</c> carries R11.67's bound sentence and <c>NOT WALKED</c>, in context.</summary>
    [Fact]
    public void R11_67_RLW32_TheHelpOfAnswerStatesTheBound()
    {
        var help = PublishedReadersLastWords.Folded(Help.For("answer") ?? string.Empty);
        var missing = new[] { PublishedReadersLastWords.BoundSentence(), PublishedReadersLastWords.WalkWords().NotWalked }.Where(s => !help.Contains(s, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"Expected: `curia help answer` to carry {string.Join(" and ", missing.Select(m => $"\"{m}\""))}");
    }

    // ----- RLW43 -----

    /// <summary>
    /// RLW43 (added 2026-10-30, finding 12). The text-that-stands clause says R10.91's words of a digest-only entry wherever it names one: a root whose successor
    /// is a digest the members do not hold states the words beside the digest in the clause that names it and again beside the same digest in the revision
    /// clause before it; a successor with a post id that the thread does not hold (R5a's shape: served, in another thread) keeps its words and is not called
    /// unserved (the control half); a held head is unchanged.
    /// </summary>
    [Fact]
    public void R10_91_RLW43_TheTextThatStandsClauseSaysTheWordsOfADigestOnlyEntry()
    {
        var words = PublishedReadersLastWords.DefinitionSentence();
        var problems = new List<string>();

        var digestOnly = RootLine(true);
        var times = Count(digestOnly, words);
        if (times < 2) problems.Add($"a digest the members do not hold: Expected: the words twice (the revision clause and the clause that names it) Actual: {times} in {Excerpt(digestOnly)}");
        else if (!EveryMentionHasTheWordsBeside(digestOnly, D1, words)) problems.Add($"a mention of the digest has no words beside it: {Excerpt(digestOnly)}");

        // R5a's shape: a successor with a post id that the thread does not hold is served.
        var root = Post(RootId, "sha256:" + new string('0', 64), [new Successor(D2, R2)]);
        var elsewhere = Supersession.RootLine(root, [root])?.Text ?? string.Empty;
        if (elsewhere.Contains("not served", StringComparison.Ordinal) || elsewhere.Contains(words, StringComparison.Ordinal)) problems.Add($"a served successor in another thread is called unserved: {Excerpt(elsewhere)}");
        if (!elsewhere.Contains(DisplayLiteral.Of(R2), StringComparison.Ordinal)) problems.Add($"a served successor in another thread is no longer named: {Excerpt(elsewhere)}");

        var held = RootLine(false);
        if (held.Contains("not served", StringComparison.Ordinal) || !held.Contains("the text that stands among the posts this thread holds is post", StringComparison.Ordinal)) problems.Add($"a held head changed: {Excerpt(held)}");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
