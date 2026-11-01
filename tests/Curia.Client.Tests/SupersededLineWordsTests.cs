using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G47, R10.89), probes CHN39 to CHN43: the <c>superseded</c> line of <c>curia recheck</c> says what the batch established. The original
/// is <c>still served</c> and never <c>stands</c>; a branch the Forum names with a digest and no post id is named, said <c>not served</c> and pointed at
/// <c>recheck</c> of its digest; the verb's help defines <c>withheld</c> and <c>not served</c>; and the exit code of a superseded or FORKED chain stays 0.
/// The words are R10.89's code spans, read from the errata by <see cref="PublishedChainWords"/>, and the set of words to define is the spans of the
/// sentence that requires the definition, so a word added to it reddens CHN43. The items are scripted, so the oracle is the digests and ids each row writes.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SupersededLineWordsTests : IDisposable
{
    private const string OriginalId = "01CHNORIGINAL00000000001A";
    private const string RevisionOne = "01CHNREVISION00000000001A";
    private const string RevisionTwo = "01CHNREVISION00000000002A";

    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Canon() =>
        "{\"author\":\"urn:curia:agent:alice\",\"board\":\"b\",\"body\":\"A body.\",\"code_blocks\":[],\"content_type\":\"agent-authored/untrusted\","
        + "\"created_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"kind\":\"question\",\"nonce\":\"n0\",\"refs\":[],\"tags\":[],\"title\":\"Question\",\"v\":1}";

    private static string Sha(string canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>One <c>superseded</c> item for a question with these successors; a null post id is a branch the Forum names without one.</summary>
    private (string[] Sent, ImmutableArray<CitationDocument> Items, CitationReport Report) Superseded(params (string Digest, string? PostId)[] successors)
    {
        var canonical = Canon();
        var digest = Sha(canonical);
        var list = successors.Select(s => new Successor(s.Digest, s.PostId)).ToImmutableArray();
        var item = StubLog.BatchItem(
            digest, "superseded", successors.Select(s => s.Digest), successors.Length > 1, _log.BatchPostJson(OriginalId, canonical, digest, list));
        _log.BatchAnswer = _ => [item];
        string[] sent = [digest];
        var result = _log.Client().BatchAsync(sent, MarkingMode.None, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(result.TryGetValue(out var served, out var refusal), refusal?.Summary);
        return (sent, served, CitationLines.For(sent, served, DateTimeOffset.UnixEpoch));
    }

    private static readonly (string Name, (string Digest, string? PostId)[] Successors)[] Shapes =
    [
        ("one served revision", [(Sha("r1"), RevisionOne)]),
        ("a fork of two served", [(Sha("r1"), RevisionOne), (Sha("r2"), RevisionTwo)]),
        ("a fork with one branch not served", [(Sha("r1"), RevisionOne), (Sha("r2"), null)]),
    ];

    /// <summary>CHN39. The line says <c>still served</c> and not <c>stands</c>, and FORKED where there are two or more.</summary>
    [Fact]
    public void R10_89_CHN39_TheSupersededLineSaysWhatItEstablished()
    {
        var (say, notSay) = PublishedChainWords.SupersededWords();
        var problems = new List<string>();
        foreach (var (name, successors) in Shapes)
        {
            var line = Assert.Single(Superseded(successors).Report.Lines).Text.Text;
            if (!line.Contains(say, StringComparison.Ordinal)) problems.Add($"{name}: lacks \"{say}\"");
            if (Regex.IsMatch(line, $@"\b{Regex.Escape(notSay)}\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))) problems.Add($"{name}: says \"{notSay}\"");
            if ((successors.Length > 1) != line.Contains("FORKED", StringComparison.Ordinal)) problems.Add($"{name}: FORKED is {(successors.Length > 1 ? "missing" : "present")}");
            if (problems.Count > 0 && problems[^1].StartsWith(name, StringComparison.Ordinal)) problems[^1] += $" [line: {line}]";
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CHN40. A branch the Forum names without a post id is named by its digest, said <c>not served</c>, and pointed at <c>recheck</c> of the digest saying
    /// whether it is <c>withheld</c>; a line whose branches are all served has no such tail.
    /// </summary>
    [Fact]
    public void R10_89_CHN40_AnUnservedBranchIsNamedAndPointed()
    {
        var words = PublishedChainWords.DefinedWords();
        var (_, successors) = Shapes[2];
        var unserved = Assert.Single(successors, s => s.PostId is null).Digest;

        var line = Assert.Single(Superseded(successors).Report.Lines).Text.Text;
        var allServed = Assert.Single(Superseded(Shapes[1].Successors).Report.Lines).Text.Text;

        var missing = new[] { unserved, "not served", PublishedChainWords.RecheckVerb(), "withheld" }.Where(w => !line.Contains(w, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"the line for a branch without a post id lacks {string.Join(", ", missing.Select(m => $"\"{m}\""))}: {line}");
        Assert.Contains(words, w => w == "not served");
        Assert.DoesNotContain("not served", allServed, StringComparison.Ordinal);
    }

    /// <summary>CHN41, the CLI's half. <c>curia recheck</c> prints the line R10.89 requires (the adapter's half is in <c>RecheckStandingToolTests</c>).</summary>
    [Fact]
    public async Task R10_89_CHN41_CuriaRecheckPrintsStillServed()
    {
        var (say, _) = PublishedChainWords.SupersededWords();
        var (sent, items, _) = Superseded(Shapes[0].Successors);

        var (_, stdout, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck(sent, items)));

        Assert.True(stdout.Contains(say, StringComparison.Ordinal), $"Expected: \"{say}\" Actual: {stdout}");
    }

    /// <summary>CHN42 (a control, green now). A superseded chain and a FORKED one exit 0; a withheld citation exits 5.</summary>
    [Fact]
    public async Task R10_89_CHN42_TheExitCodeOfASupersededChainIsStill0()
    {
        var exits = new List<int>();
        foreach (var (_, successors) in Shapes.Take(2))
        {
            var (sent, items, _) = Superseded(successors);
            exits.Add((await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck(sent, items)))).Exit);
        }

        var digest = Sha(Canon());
        _log.BatchAnswer = _ => [StubLog.BatchItem(digest, "withheld")];
        var withheld = await _log.Client().BatchAsync([digest], MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(withheld.TryGetValue(out var served, out _));
        exits.Add((await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck([digest], served)))).Exit);

        Assert.Equal([0, 0, 5], exits);
    }

    /// <summary>
    /// CHN43. The words R10.89 requires the help to define are each defined in <c>recheck</c>'s help: the word, then <c>means</c>, <c>is</c>, a colon,
    /// a dash or an equals sign. The list is the spans of R10.89's sentence, so a word added there reddens this fact.
    /// </summary>
    [Fact]
    public void R10_89_CHN43_TheHelpDefinesTheWordsWhereTheyAreMet()
    {
        var help = Help.For("recheck") ?? throw new Xunit.Sdk.XunitException("no help for recheck");
        var undefined = PublishedChainWords.DefinedWords()
            .Where(w => !Regex.IsMatch(help, Regex.Escape(w) + @"\s*(means|is|:|--|=)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)))
            .ToArray();

        Assert.True(undefined.Length == 0, $"curia recheck's help does not define {string.Join(", ", undefined.Select(u => $"\"{u}\""))}");
    }
}
