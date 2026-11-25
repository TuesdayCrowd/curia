using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Errata G81 (R9.72), plan KWC1, KWC2, KWC14 and KWC17: the rule file, the three copies of the list, the two artifacts that measure it, and the runner's own verdict. The
/// facts that read <c>python3</c> run it through a process and <b>fail, never skip</b>, without one: the process boundary is the property under test, as for R11.20's signer tests.
///
/// <para>No message of a fact here prints a query's text, a term of the blind material, a page share or a verdict: a failure names an id and a member (plan Amendment 1, ruling 0).
/// The blind material's measures are computed only after RESULTS.md carries its section (KWC14c), which is Task 4's; until then that fact is red at its first assertion.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class StopwordRuleTests
{
    private static string Retrieval => StopwordMeasure.Retrieval;

    private static string Script => Path.Combine(StopwordMeasure.RepoRoot(), "tools", "retrieval", "lexical-measure.py");

    private static string QueriesPath => Path.Combine(Retrieval, "blind-queries.jsonl");

    private static ImmutableArray<string> Block => StopwordMeasure.ListBlock(StopwordMeasure.RuleText);

    private static HashSet<string> List => [.. Block];

    /// <summary>Runs <c>python3 -I tools/retrieval/lexical-measure.py</c> with the arguments; throws when python3 is absent or the script exits non-zero.</summary>
    private static string Python(params string[] arguments)
    {
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Script);
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("python3 did not start");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? stdout : throw new InvalidOperationException($"lexical-measure.py exited {process.ExitCode}: {stderr.GetAwaiter().GetResult()}");
    }

    // ----- KWC1 -----

    /// <summary>
    /// KWC1. The rule file's list block parses (each group's stated size is its words), every word is lower-case ASCII and unique, the block is sorted, each word is one token under
    /// <c>Tokenize</c>, the count is <c>list_size</c>, and <c>stopwords.txt</c> is the block word for word with no trailing line.
    /// </summary>
    [Fact]
    public void KWC1_TheListIsWellFormedAndStopwordsTxtIsTheRuleFilesBlock()
    {
        var words = Block;
        Assert.True(words.Length > 0, "Expected: the rule file holds a list block  Actual: none parsed");
        Assert.All(words, w => Assert.Matches("^[a-z]+$", w));
        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(words.OrderBy(w => w, StringComparer.Ordinal), words);
        foreach (var w in words)
            Assert.True(LexicalSearch.Tokenize(w).SequenceEqual([w]), $"Expected: Tokenize(\"{w}\") is the one token \"{w}\"  Actual: {string.Join(",", LexicalSearch.Tokenize(w))}");
        Assert.Equal((int)StopwordMeasure.Params(StopwordMeasure.RuleText)["list_size"], words.Length);

        var file = File.ReadAllText(Path.Combine(Retrieval, "stopwords.txt"), Encoding.UTF8);
        Assert.Equal(string.Join('\n', words), file);
    }

    /// <summary>
    /// KWC1. The parameter table parses to nine rows, every parameter the prose names is in it, and every row but <c>list_size</c> (which the prose states as 111 and the fact above counts)
    /// is named in the prose; and <c>keep_window</c> is the constant the channel serves as its default page, so the rule and the code cannot drift.
    /// </summary>
    [Fact]
    public void KWC1_TheParameterTableIsTheProsesAndKeepWindowIsTheDefaultPage()
    {
        var text = StopwordMeasure.RuleText;
        var table = StopwordMeasure.Params(text).Values.Keys.ToImmutableSortedSet(StringComparer.Ordinal);
        var prose = StopwordMeasure.ParametersNamedInProse(text);

        Assert.Equal(9, table.Count);
        Assert.True(prose.IsSubsetOf(table), $"Expected: every parameter the prose names is a row  Actual: not in the table: {string.Join(", ", prose.Except(table))}");
        Assert.True(table.Except(["list_size"]).All(prose.Contains), $"Expected: every row but list_size is named in the prose  Actual: not named: {string.Join(", ", table.Except(["list_size"]).Except(prose))}");
        Assert.Equal(LexicalSearch.DefaultLimit, StopwordMeasure.Params(text).Window);
    }

    // ----- KWC2: three artifacts, one list -----

    /// <summary>KWC2 (Domain). The Domain's <c>Curia.Domain.Search.StopWords</c> holds the block word for word. It is read by name (<c>Words</c>), as a probe of what does not exist yet must be: a compile error is not an assertion.</summary>
    [Fact]
    public void KWC2_TheDomainsStopWordsIsTheRuleFilesBlock()
    {
        var type = typeof(LexicalSearch).Assembly.GetType("Curia.Domain.Search.StopWords");
        Assert.True(type is not null, "Expected: Curia.Domain.Search.StopWords holds the list (a constant, no I/O)  Actual: no such type in Curia.Domain");

        var member = type.GetProperty("Words", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? type.GetField("Words", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        Assert.True(member is System.Collections.IEnumerable, "Expected: StopWords.Words is the list  Actual: no public static Words member");
        var words = ((System.Collections.IEnumerable)member).Cast<string>().OrderBy(w => w, StringComparer.Ordinal).ToArray();
        Assert.Equal(Block, words);
    }

    /// <summary>KWC2 (Python). <c>python3 tools/retrieval/lexical-measure.py --print-list</c> prints the block, which the script reads from the rule file and from nothing else.</summary>
    [Fact]
    public void KWC2_ThePythonReferencePrintsTheRuleFilesBlock()
    {
        var printed = Python("--print-list").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Block, printed);
    }

    /// <summary>KWC2 (errata). The digest R9.72 states is the SHA-256 of the block's words joined by one line feed with no trailing one (the G66 / NDT13 form: the errata is parsed at test time).</summary>
    [Fact]
    public void KWC2_TheDigestTheErrataStatesIsTheBlocksSha256()
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R9.72**");
        var stated = DigestSpan().Match(paragraph);
        Assert.True(stated.Success, "Expected: R9.72 states a SHA-256 in a code span  Actual: none");

        var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Block))));
        Assert.Equal(actual, stated.Groups[1].Value);
    }

    [GeneratedRegex("SHA-256 `([0-9a-f]{64})`", RegexOptions.CultureInvariant)]
    private static partial Regex DigestSpan();

    // ----- KWC14: the two artifacts, and the committed reference -----

    private static (ImmutableArray<SearchablePost> Corpus, ImmutableArray<QueryOutcome> Outcomes) CSharp()
    {
        var (corpus, queries, targets) = StopwordMeasure.Build(Retrieval, QueriesPath);
        return (corpus, StopwordMeasure.Run(corpus, queries, targets));
    }

    private static string Flat(ImmutableArray<(long Seq, int Score)> hits) => string.Join(',', hits.Select(h => $"{h.Seq}:{h.Score}"));

    private static string FlatOf(JsonElement flat)
    {
        var numbers = flat.EnumerateArray().Select(e => e.GetInt64()).ToArray();
        return string.Join(',', Enumerable.Range(0, numbers.Length / 2).Select(i => $"{numbers[2 * i]}:{numbers[(2 * i) + 1]}"));
    }

    private static string Words(JsonElement array) => string.Join(',', array.EnumerateArray().Select(e => e.GetString()));

    /// <summary>
    /// KWC14 (a). The two artifacts agree on every integer of the committed material: the corpus's size and order, and for each query its tokens, terms, ignored words, whether it had a
    /// token, its target's position, and the hits and scores with the list and without it, in rank order. No tolerance, because there is no float. A disagreement names the id
    /// and the member and never a text, a term or a count.
    /// </summary>
    [Fact]
    public void KWC14_ThePythonReferenceAndTheCSharpAgreeOnEveryInteger()
    {
        var lines = Python().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var (corpus, outcomes) = CSharp();

        var header = lines[0];
        Assert.Equal(corpus.Length, header.GetProperty("size").GetInt32());
        Assert.Equal(corpus.Select(p => p.PostId), header.GetProperty("post_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(outcomes.Length, lines.Count - 1);

        var problems = new List<string>();
        for (var i = 0; i < outcomes.Length; i++)
        {
            var o = outcomes[i];
            var j = lines[i + 1];
            var who = o.Query.Id + "/" + o.Query.Form;
            void Same(string member, string expected, string actual)
            {
                if (!string.Equals(expected, actual, StringComparison.Ordinal)) problems.Add($"{who}: {member}");
            }

            Same("id", o.Query.Id + "/" + o.Query.Form, j.GetProperty("id").GetString() + "/" + j.GetProperty("form").GetString());
            Same("tokens", string.Join(',', o.Tokens), Words(j.GetProperty("tokens")));
            Same("terms", string.Join(',', o.Terms), Words(j.GetProperty("terms")));
            Same("ignored", string.Join(',', o.Ignored), Words(j.GetProperty("ignored")));
            Same("had_token", o.HadToken.ToString(), j.GetProperty("had_token").GetBoolean().ToString());
            Same("all_ignored", o.AllIgnored.ToString(), j.GetProperty("all_ignored").GetBoolean().ToString());
            Same("target_seq", o.TargetSeq?.ToString(CultureInfo.InvariantCulture) ?? "none", j.GetProperty("target_seq").ValueKind == JsonValueKind.Null ? "none" : j.GetProperty("target_seq").GetInt64().ToString(CultureInfo.InvariantCulture));
            Same("without", Flat(o.Without), FlatOf(j.GetProperty("without")));
            Same("with", Flat(o.With), FlatOf(j.GetProperty("with")));
        }

        Assert.True(problems.Count == 0, "Expected: the two artifacts equal on every integer  Actual: differing members (id/form: member) " + string.Join("; ", problems.Take(20)));
    }

    private static string ReferencePath => Path.Combine(Retrieval, "measured", "lexical-stopwords.reference.jsonl");

    /// <summary>KWC14 (b). Fresh output of the script equals the committed <c>measured/lexical-stopwords.reference.jsonl</c> byte for byte, so the committed file cannot go stale against the script that claims to have made it.</summary>
    [Fact]
    public void KWC14_TheCommittedReferenceIsWhatTheScriptWritesNow()
    {
        Assert.True(File.Exists(ReferencePath), "Expected: conformance/retrieval/measured/lexical-stopwords.reference.jsonl exists  Actual: no such file");

        Assert.True(File.ReadAllBytes(ReferencePath).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(Python())), "Expected: the committed reference equals the script's output byte for byte  Actual: they differ");
    }

    private const string Begin = "<!-- lexical-stopwords:begin -->";
    private const string End = "<!-- lexical-stopwords:end -->";

    /// <summary>
    /// KWC14 (c). RESULTS.md carries a section between the <c>lexical-stopwords</c> markers, and it equals what the runner computes now; the runner and the script agree on the rule's
    /// numbers and verdict. The first assertion is the only one that runs before Task 4: the measures are not computed until the section exists.
    /// </summary>
    [Fact]
    public void KWC14_ResultsMdHoldsTheSectionTheRunnerComputesNow()
    {
        var results = File.ReadAllText(Path.Combine(Retrieval, "RESULTS.md"), Encoding.UTF8);
        var from = results.IndexOf(Begin, StringComparison.Ordinal);
        var to = results.IndexOf(End, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "Expected: RESULTS.md holds a section between the lexical-stopwords markers  Actual: no such section");

        var (corpus, outcomes) = CSharp();
        var measures = StopwordMeasure.Compute(outcomes, corpus.Length, StopwordMeasure.Params(StopwordMeasure.RuleText));
        var section = results[(from + Begin.Length)..to].Trim();
        Assert.Equal(StopwordMeasure.Render(measures), section);

        using var python = JsonDocument.Parse(Python("--measures"));
        var verdict = python.RootElement.GetProperty("verdict").GetString();
        Assert.Equal(measures.Verdict.ToString().ToUpperInvariant(), verdict!.ToUpperInvariant());
        Assert.Equal(measures.KeepOnTopic.Base, python.RootElement.GetProperty("keep_on_topic").GetProperty("base").GetInt32());
        Assert.Equal(measures.KeepOnTopic.Kept, python.RootElement.GetProperty("keep_on_topic").GetProperty("kept").GetInt32());
        Assert.Equal(measures.KeepHazard.Lost, python.RootElement.GetProperty("keep_hazard").GetProperty("lost").GetInt32());
        Assert.Equal(measures.Removal.With, python.RootElement.GetProperty("removal").GetProperty("with").GetInt32());
    }

    // ----- KWC17: the runner's verdict, by hand -----

    private static ImmutableArray<SearchablePost> SixPosts() =>
    [
        Post(0, "mixed", "the the the the the"),
        Post(1, "cats", "the thing"),
        Post(2, "kestrel", "bird"),
        Post(3, "nothing", "heron " + string.Concat(Enumerable.Repeat("the ", 20))),
        Post(4, "x", "heron heron"),
        Post(5, "y", "heron heron"),
    ];

    private static SearchablePost Post(int seq, string title, string body) =>
        new("p" + seq.ToString(CultureInfo.InvariantCulture), "sha256:" + seq.ToString(CultureInfo.InvariantCulture), "b", PostKind.Question, title, body, [], "a", seq);

    private static MaterialQuery Target(string id, string text, string target) => new(id, "target", "terse", text, target);

    private static ImmutableArray<QueryOutcome> Outcomes(IEnumerable<MaterialQuery> queries) =>
        StopwordMeasure.Run(SixPosts(), queries, Enumerable.Range(0, 6).ToDictionary(i => "p" + i.ToString(CultureInfo.InvariantCulture), i => (long)i));

    private static RuleParams Parameters => StopwordMeasure.Params(StopwordMeasure.RuleText).With("keep_window", 2);

    /// <summary>
    /// KWC17 (the Keep's definition, by hand, over six posts and window 2). <c>the cat</c> targets p1, which holds <c>the</c> and no <c>cat</c>: listed without the list only, so
    /// <b>stopword-only</b> and not in the base. <c>kestrel</c> targets p2: a real find at rank 1 both ways, <b>kept</b>. <c>the heron</c> targets p3, which says <c>the</c> twenty times
    /// and <c>heron</c> once: rank 1 without the list (21), rank 3 with it (p4 and p5 hold <c>heron</c> twice), so it is a real find <b>lost</b> to the window. <c>the</c> targets p0: all-ignored,
    /// listed without and not with, so stopword-only and <b>all-ignored</b>. Real 2, base 2, kept 1, stopword-only 2, all-ignored 1, and a base under 30: <b>undecided</b>.
    /// </summary>
    [Fact]
    public void KWC17_AStopwordOnlyMatchIsNotAFindARealFindThePushedPastTheWindowIsLostAndASmallBaseIsUndecided()
    {
        var outcomes = Outcomes([Target("t1", "the cat", "p1"), Target("t2", "kestrel", "p2"), Target("t3", "the heron", "p3"), Target("t4", "the", "p0")]);

        var m = StopwordMeasure.Compute(outcomes, 6, Parameters);

        Assert.Equal((2, 2, 2, 1, 1), (m.KeepOnTopic.Real, m.KeepOnTopic.Base, m.KeepOnTopic.StopwordOnly, m.KeepOnTopic.Kept, m.KeepOnTopic.AllIgnored));
        Assert.Equal(StopwordVerdict.Undecided, m.Verdict);
    }

    private static ImmutableArray<QueryOutcome> HundredAndOffTopic(int kept, int lost)
    {
        var queries = new List<MaterialQuery>();
        for (var i = 0; i < kept; i++) queries.Add(new MaterialQuery($"k{i}", "target", "terse", "kestrel", "p2"));
        for (var i = 0; i < lost; i++) queries.Add(new MaterialQuery($"l{i}", "target", "terse", "the heron", "p3"));
        for (var i = 0; i < 30; i++) queries.Add(new MaterialQuery($"o{i}", "off-topic", "query", "the zzz", null));
        return Outcomes(queries);
    }

    /// <summary>
    /// KWC17. A base of a hundred with six real finds lost is 94% kept and the verdict is <b>no</b> for the Keep and for nothing else (thirty off-topic queries make Removal hold: the
    /// page is the three posts that say <c>the</c> without the list and none with it); two lost is 98%, the mean reciprocal rank falls by 0.0133 (each lost find drops from 1 to 1/3), and the
    /// same table <b>ships</b>. The Keep's own gate has teeth in both directions.
    /// </summary>
    [Fact]
    public void KWC17_ABaseOfAHundredWithSixLostIsNoAndWithTwoLostItShips()
    {
        var three = StopwordMeasure.Compute(HundredAndOffTopic(94, 6), 6, Parameters);
        var two = StopwordMeasure.Compute(HundredAndOffTopic(98, 2), 6, Parameters);

        Assert.True(three.Removal.Ok && three.KeepOnTopic.BaseOk, "Expected: Removal holds and the base is 100  Actual: " + StopwordMeasure.Render(three));
        Assert.False(three.KeepOnTopic.KeptOk);
        Assert.True(two.KeepOnTopic.MrrOk);
        Assert.Equal(StopwordVerdict.No, three.Verdict);
        Assert.True(two.KeepOnTopic.KeptOk, "Expected: 98 of 100 is at least 0.95 and the MRR holds  Actual: " + StopwordMeasure.Render(two));
        Assert.Equal(StopwordVerdict.Ships, two.Verdict);
    }

    /// <summary>KWC17. A hazard query that only a listed word held is lost, an all-ignored one included, and the loss over <c>ceil(0.20 m)</c> is <b>no</b> though every other gate holds.</summary>
    [Fact]
    public void KWC17_AHazardLossOverAFifthIsNoAndAnAllIgnoredQueryCountsAsLost()
    {
        var queries = new List<MaterialQuery>();
        for (var i = 0; i < 30; i++) queries.Add(new MaterialQuery($"k{i}", "target", "terse", "kestrel", "p2"));
        for (var i = 0; i < 30; i++) queries.Add(new MaterialQuery($"o{i}", "off-topic", "query", "the zzz", null));
        for (var i = 0; i < 2; i++) queries.Add(new MaterialQuery($"hl{i}", "hazard", "terse", "the", "p0"));
        for (var i = 0; i < 8; i++) queries.Add(new MaterialQuery($"hk{i}", "hazard", "terse", "kestrel", "p2"));

        var m = StopwordMeasure.Compute(Outcomes(queries), 6, Parameters);

        Assert.Equal((10, 2, 2, 2), (m.KeepHazard.M, m.KeepHazard.Lost, m.KeepHazard.Allowed, m.KeepHazard.LostAllIgnored));
        Assert.Equal(StopwordVerdict.Ships, m.Verdict);

        queries.Add(new MaterialQuery("hl2", "hazard", "terse", "the", "p0"));
        var over = StopwordMeasure.Compute(Outcomes(queries), 6, Parameters);
        Assert.Equal((11, 3, 3), (over.KeepHazard.M, over.KeepHazard.Lost, over.KeepHazard.Allowed));
        Assert.Equal(StopwordVerdict.Ships, over.Verdict);

        queries.Add(new MaterialQuery("hl3", "hazard", "terse", "the", "p0"));
        var no = StopwordMeasure.Compute(Outcomes(queries), 6, Parameters);
        Assert.Equal((12, 4, 3), (no.KeepHazard.M, no.KeepHazard.Lost, no.KeepHazard.Allowed));
        Assert.Equal(StopwordVerdict.No, no.Verdict);
    }

    /// <summary>KWC17. A query with no token is a listing: reported, and excluded from every measure.</summary>
    [Fact]
    public void KWC17_AListingIsReportedAndExcluded()
    {
        var queries = new List<MaterialQuery> { new("lst", "off-topic", "query", "???", null) };
        for (var i = 0; i < 30; i++) queries.Add(new MaterialQuery($"o{i}", "off-topic", "query", "the zzz", null));

        var m = StopwordMeasure.Compute(Outcomes(queries), 6, Parameters);

        Assert.Equal(["lst"], m.Listings);
        Assert.Equal(30, m.Removal.Queries);
    }
}
