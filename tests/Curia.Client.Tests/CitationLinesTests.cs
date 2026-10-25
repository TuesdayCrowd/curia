using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G35 (R9.34): what a reader of R9.10's batch prints for each element, and what it refuses. The oracles are the test's own: the ids and
/// canonical text it chose for the posts it has the stub serve, and the digest it computed from that text with the BCL, never the report's
/// statement of itself. A negative probe has a control that must pass.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CitationLinesTests : IDisposable
{
    private const string Marker = "ZQ9AUTHOR";
    private const string IdQuestion = "01ZQPB0QUESTION000000000001";
    private const string IdRevisionOne = "01ZQPB0REVISION000000000001";

    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A question's canonical form as JCS writes it (members in ordinal order), so a digest computed over this text is the digest of its canonical form.</summary>
    private static string Canon(string title, string body = "A body.", string tags = "[]")
    {
        var text = "{\"author\":\"urn:curia:agent:alice\",\"board\":\"b\",\"body\":" + Quote(body)
            + ",\"code_blocks\":[],\"content_type\":\"agent-authored/untrusted\",\"created_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"kind\":\"question\","
            + "\"nonce\":\"n0\",\"refs\":[],\"tags\":" + tags + ",\"title\":" + Quote(title) + ",\"v\":1}";

        // Non-vacuity of the oracle: the text really is in the order JCS writes, or its hash is not its canonical form's.
        using var document = System.Text.Json.JsonDocument.Parse(text);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.True(names.SequenceEqual(names.Order(StringComparer.Ordinal)), "the fixture's canonical text is not in ordinal member order");
        return text;
    }

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Sha(string canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private string Current(string digest, string postId, string canonical, long? disputedBy = null, string? digestServed = null, string rendered = "r") =>
        StubLog.BatchItem(digest, "current", postJson: _log.BatchPostJson(postId, canonical, digestServed ?? digest, disputedBy: disputedBy, rendered: rendered));

    private async Task<CitationReport> ReportAsync(string[] sent, params string[] items)
    {
        _log.BatchAnswer = _ => items;
        var result = await _log.Client().BatchAsync(sent, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(result.TryGetValue(out var served, out var refusal), refusal?.Summary);
        return CitationLines.For(sent, served);
    }

    private static string Joined(CitationReport report) =>
        string.Join('\n', report.Lines.Select(l => l.Text.Text)) + "\n" + report.Error?.Detail + report.Error?.Title;

    /// <summary>One digest and one served current item for a numbered question.</summary>
    private (string Digest, string Canonical, string Item) Question(int n, string postId)
    {
        var canonical = Canon($"Question {n}");
        var digest = Sha(canonical);
        return (digest, canonical, Current(digest, postId, canonical));
    }

    [Fact]
    public async Task R9_34_EachOfTheFiveStatesIsNamedInOrderWithNothingOmitted()
    {
        var current = Question(1, IdQuestion);
        var original = Canon("Question 2");
        var originalDigest = Sha(original);
        var revisionDigest = "sha256:" + new string('b', 64);
        var superseded = StubLog.BatchItem(
            originalDigest, "superseded", [revisionDigest], false,
            _log.BatchPostJson("01ZQPB1ORIGINAL0000000000001", original, originalDigest, [new Successor(revisionDigest, IdRevisionOne)]));
        var withheldDigest = "sha256:" + new string('c', 64);
        var unknownDigest = "sha256:" + new string('d', 64);
        string[] sent = [current.Digest, originalDigest, withheldDigest, unknownDigest, "nope"];
        string[] items =
        [
            current.Item,
            superseded,
            StubLog.BatchItem(withheldDigest, "withheld"),
            StubLog.BatchItem(unknownDigest, "unknown"),
            StubLog.BatchItem(null, "malformed"),
        ];

        var report = await ReportAsync(sent, items);

        Assert.Null(report.Error);
        string[] words = ["current", "superseded", "withheld", "unknown", "malformed"];
        Assert.Equal(words.Length, report.Lines.Length);
        for (var i = 0; i < words.Length; i++)
        {
            Assert.Equal(words[i], report.Lines[i].State);
            Assert.StartsWith(words[i], report.Lines[i].Text.Text, StringComparison.Ordinal);
        }

        Assert.Contains("digests[4]", report.Lines[4].Text.Text, StringComparison.Ordinal);

        // Control: a sixth, well-formed current element prints a sixth line.
        var sixth = Question(6, "01ZQPB1SIXTH000000000000001");
        var withSixth = await ReportAsync([.. sent, sixth.Digest], [.. items, sixth.Item]);
        Assert.Equal(6, withSixth.Lines.Length);
    }

    [Fact]
    public async Task R9_34_ACurrentItemNamesItsPostIdAndSaysTheDigestWasRecomputed()
    {
        var (digest, _, item) = Question(1, IdQuestion);

        var report = await ReportAsync([digest], item);

        Assert.Null(report.Error);
        var line = Assert.Single(report.Lines).Text.Text;
        Assert.Contains(IdQuestion, line, StringComparison.Ordinal);
        Assert.Contains("digest recomputed here: matches", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_34_ASupersededItemNamesEachSuccessorsPostIdAndADigestOnlySuccessorIsNotServed()
    {
        var original = Canon("Question 3");
        var digest = Sha(original);
        var (d1, d2) = ("sha256:" + new string('1', 64), "sha256:" + new string('2', 64));
        const string Id1 = "01ZQPB3SUCCESSOR00000000001";
        var forked = StubLog.BatchItem(
            digest, "superseded", [d1, d2], true,
            _log.BatchPostJson("01ZQPB3ORIGINAL0000000000001", original, digest, [new Successor(d1, Id1), new Successor(d2, null)]));

        var report = await ReportAsync([digest], forked);

        // The ids are not in the item's `successors` (digests alone), so a reader that took them from there could not name one.
        Assert.DoesNotContain(Id1, d1 + d2, StringComparison.Ordinal);
        var line = Assert.Single(report.Lines).Text.Text;
        Assert.Contains(Id1, line, StringComparison.Ordinal);
        Assert.Contains(d2, line, StringComparison.Ordinal);
        Assert.Contains("not served", line, StringComparison.Ordinal);
        Assert.Contains("FORKED", line, StringComparison.Ordinal);

        // Control: one served successor is neither forked nor not served.
        var single = StubLog.BatchItem(
            digest, "superseded", [d1], false,
            _log.BatchPostJson("01ZQPB3ORIGINAL0000000000001", original, digest, [new Successor(d1, Id1)]));
        var control = Assert.Single((await ReportAsync([digest], single)).Lines).Text.Text;
        Assert.Contains(Id1, control, StringComparison.Ordinal);
        Assert.DoesNotContain("FORKED", control, StringComparison.Ordinal);
        Assert.DoesNotContain("not served", control, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_34_ADisputedItemIsMarkedDisputedAndIsNotCurrent()
    {
        var canonical = Canon("Question 4");
        var digest = Sha(canonical);

        var report = await ReportAsync([digest], Current(digest, IdQuestion, canonical, disputedBy: 42));

        var line = Assert.Single(report.Lines);
        Assert.True(line.Disputed);
        Assert.Contains("disputed", line.Text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("cite as-is", line.Text.Text, StringComparison.Ordinal);

        // R9.34: a disputed element still names the post the Forum serves for it.
        Assert.Contains(IdQuestion, line.Text.Text, StringComparison.Ordinal);

        // Control: the same post under an active key is not disputed and is citable.
        var plain = Assert.Single((await ReportAsync([digest], Current(digest, IdQuestion, canonical))).Lines);
        Assert.False(plain.Disputed);
        Assert.Contains("cite as-is", plain.Text.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task R9_34_APositionIsZeroBasedAndAMalformedValueIsNeverEchoed(int position)
    {
        const string Hostile = "ZQ7BAD";
        var sent = new List<string>();
        var items = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            if (i == position)
            {
                sent.Add(Hostile + "-not-a-digest");
                items.Add(StubLog.BatchItem(null, "malformed"));
            }
            else
            {
                var q = Question(10 + i, $"01ZQPB5QUESTION0000000000{i}1");
                sent.Add(q.Digest);
                items.Add(q.Item);
            }
        }

        var report = await ReportAsync([.. sent], [.. items]);

        Assert.Null(report.Error);
        Assert.Equal(3, report.Lines.Length);
        Assert.Contains($"digests[{position}]", report.Lines[position].Text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, Joined(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_34_NothingAnAuthorWroteReachesALine()
    {
        var canonical = Canon($"Title {Marker}", $"Body {Marker}", $"[\"{Marker}\"]");
        var digest = Sha(canonical);

        var report = await ReportAsync([digest], Current(digest, IdQuestion, canonical, rendered: $"Rendered {Marker}"));

        // Non-vacuity: a line was printed, and the marker is in what the stub served.
        Assert.Single(report.Lines);
        Assert.Contains(Marker, canonical, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, Joined(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_34_AValueTheForumSuppliedIsADisplayLiteral()
    {
        var hostileId = "01ZQPB7\nPB7\n" + char.ConvertFromUtf32(0xE000) + "ID";
        var hostileSuccessor = "01ZQPB7\nSUCCESSOR\n" + char.ConvertFromUtf32(0xE000) + "ID";
        var original = Canon("Question 7");
        var digest = Sha(original);
        var d1 = "sha256:" + new string('7', 64);
        var item = StubLog.BatchItem(
            digest, "superseded", [d1], false,
            _log.BatchPostJson(hostileId, original, digest, [new Successor(d1, hostileSuccessor)]));

        var report = await ReportAsync([digest], item);

        var line = Assert.Single(report.Lines).Text.Text;
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.Contains(Curia.Canon.Json.DisplayLiteral.Of(hostileId), line, StringComparison.Ordinal);
        Assert.Contains(Curia.Canon.Json.DisplayLiteral.Of(hostileSuccessor), line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("position")]
    public async Task R9_34_AResponseThatAnswersOtherThanWhatWasSentIsRefusedWithNoStanding(string shape)
    {
        var (a, b, c) = (Question(20, "01ZQPB8QUESTION0000000000A"), Question(21, "01ZQPB8QUESTION0000000000B"), Question(22, "01ZQPB8QUESTION0000000000C"));
        var (sent, items) = shape switch
        {
            "short" => (new[] { a.Digest, b.Digest, c.Digest }, new[] { a.Item, b.Item }),
            "extra" => (new[] { a.Digest, b.Digest }, new[] { a.Item, b.Item, c.Item }),
            _ => (new[] { a.Digest, b.Digest }, new[] { a.Item, Current(a.Digest, "01ZQPB8QUESTION0000000000A", a.Canonical) }),
        };

        // Control: the matching response prints one line per element.
        var matching = await ReportAsync(sent, [.. sent.Select(d => d == a.Digest ? a.Item : d == b.Digest ? b.Item : c.Item)]);
        Assert.Null(matching.Error);
        Assert.Equal(sent.Length, matching.Lines.Length);

        var report = await ReportAsync(sent, items);

        Assert.NotNull(report.Error);
        Assert.Empty(report.Lines);
        var said = report.Error.Detail ?? string.Empty;
        if (shape == "position")
            Assert.Contains("digests[1]", said, StringComparison.Ordinal);
        else
            Assert.True(said.Contains(sent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) && said.Contains(items.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), $"the error names neither count: {said}");
    }

    /// <summary>
    /// Derived from the errata, not the code: the tokens R9.34's paragraph names (FAILED, FORKED, <c>not served</c>, <c>digests[&lt;n&gt;]</c> and <c>current</c>) each
    /// appear in what the readers print for the shapes that earn them.
    /// </summary>
    [Fact]
    public async Task R9_34_TheWordsTheErrataNamesAreTheWordsPrinted()
    {
        var tokens = PublishedRecheck.R934Tokens();
        Assert.Equal(5, tokens.Length);

        var q = Question(30, IdQuestion);
        var original = Canon("Question 31");
        var originalDigest = Sha(original);
        var d1 = "sha256:" + new string('3', 64);
        var d2 = "sha256:" + new string('4', 64);
        var forked = StubLog.BatchItem(
            originalDigest, "superseded", [d1, d2], true,
            _log.BatchPostJson("01ZQPB16ORIGINAL000000000001", original, originalDigest, [new Successor(d1, "01ZQPB16SUCC00000000000001"), new Successor(d2, null)]));
        var lying = Canon("Question 32");
        var lyingAsked = Sha(Canon("Question 33"));
        var failing = Current(lyingAsked, "01ZQPB16LYING0000000000001", lying);

        var printed = new StringBuilder();
        foreach (var report in new[]
        {
            await ReportAsync([q.Digest, "nope"], q.Item, StubLog.BatchItem(null, "malformed")),
            await ReportAsync([originalDigest], forked),
            await ReportAsync([lyingAsked], failing),
        })
        {
            printed.AppendLine(Joined(report));
        }

        foreach (var token in tokens)
            Assert.True(PublishedRecheck.PatternOf(token).IsMatch(printed.ToString()), $"the errata names \"{token}\" and no line printed it:\n{printed}");
    }
}
