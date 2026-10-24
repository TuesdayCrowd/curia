using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G28, R8.66: a passage states, beside the count it prints of what it read, how many items of <c>refs</c> or
/// <c>code_blocks</c> the author signed that it did not read, and prints that statement for a post whose every item it
/// dropped. A post with nothing dropped keeps today's text byte for byte.
///
/// <para><b>Where the expectation comes from.</b> The number signed is counted from the post's canonical form by
/// <c>System.Text.Json</c>, not by <see cref="PostEnvelope"/>; the "nothing dropped" lines are the text of the build before
/// this stage.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DroppedItemsLineTests : IDisposable
{
    private const string GoodRef = """{"kind":"url","target":"https://a.example/x"}""";
    private const string WrongRef = """{"kind":"url","value":"https://b.example/y"}""";
    private const string GoodCode = """{"language":"csharp","source":"var x = 1;"}""";
    private const string WrongCode = """{"language":"csharp","text":"var y = 2;"}""";

    /// <summary>Today's lines for a post that dropped nothing (Passage.cs of the build before this stage).</summary>
    private const string RefsToday = "refs      2 reference(s) inside the block below. NOT FETCHED, and this client has no code path that would fetch one (contract clause 3).";
    private const string CodeToday = "code      2 code block(s) inside the block below. NOT EXECUTED, NOT INSTALLED (contract clause 3).";

    private readonly string _root = Directory.CreateTempSubdirectory("curia-dropped-line-").FullName;
    private readonly EnrolledAgent _agent;

    public DroppedItemsLineTests()
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

    /// <summary>A passage whose served canonical form holds <paramref name="refs"/> and <paramref name="code"/> as written (raw JSON arrays), honestly rendered.</summary>
    private (Passage Passage, int SignedRefs, int SignedCode) Passage(string refs, string code)
    {
        var draft = new PostDraft { Kind = PostKind.Question, Board = "b", Title = "t", Body = "A body." };
        Assert.True(SubmissionBuilder.Build(_agent, draft, DateTimeOffset.UnixEpoch).TryGetValue(out var signed, out var error), error?.Type);

        var envelope = Assert.IsType<JsonValue.Object>(JsonReader.Parse(signed!.Canonical.ToArray(), AdmitLimits.Default).Match(v => v, e => throw new InvalidOperationException(e.Type)));
        var replaced = envelope.Members.Where(m => m.Key is not ("refs" or "code_blocks")).ToList();
        replaced.Add(new("refs", Parse(refs)));
        replaced.Add(new("code_blocks", Parse(code)));
        Assert.True(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object([.. replaced])).TryGetValue(out var canonicalBytes, out _));
        var canonical = Encoding.UTF8.GetString(canonicalBytes.Span);

        using var document = JsonDocument.Parse(canonical);
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None) with
        {
            Signature = signed.Signature,
            Digest = signed.PrefixedDigest,
        };

        return (
            new Passage(post, new SignatureVerdict(true, "alice-1", "ok", signed.Digest, signed.PrefixedDigest)),
            document.RootElement.GetProperty("refs").GetArrayLength(),
            document.RootElement.GetProperty("code_blocks").GetArrayLength());
    }

    private static JsonValue Parse(string raw) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(raw), AdmitLimits.Default).Match(v => v, e => throw new InvalidOperationException(e.Type));

    private static string? LineOf(Passage passage, string label) =>
        passage.Render().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .TakeWhile(l => !l.StartsWith(Datamarking.OpenDelimiter, StringComparison.Ordinal))
            .FirstOrDefault(l => l.StartsWith(label + " ", StringComparison.Ordinal));

    /// <summary>R8.66 for each member and each way a post can drop: one of two, every item, and (the control) none.</summary>
    [Theory]
    [InlineData("refs", "one of two dropped")]
    [InlineData("refs", "the only item dropped")]
    [InlineData("refs", "nothing dropped")]
    [InlineData("code", "one of two dropped")]
    [InlineData("code", "the only item dropped")]
    [InlineData("code", "nothing dropped")]
    public void R8_66_APassageStatesWhatItDropped(string label, string row)
    {
        var refs = label == "refs";
        var (good, wrong) = refs ? (GoodRef, WrongRef) : (GoodCode, WrongCode);
        var items = row switch
        {
            "one of two dropped" => $"[{good},{wrong}]",
            "the only item dropped" => $"[{wrong}]",
            _ => $"[{good},{good}]",
        };
        var (passage, signedRefs, signedCode) = refs ? Passage(items, "[]") : Passage("[]", items);
        var signed = refs ? signedRefs : signedCode;
        Assert.Equal(row == "the only item dropped" ? 1 : 2, signed);

        var noun = refs ? "reference(s)" : "code block(s)";
        var line = LineOf(passage, label);

        switch (row)
        {
            case "one of two dropped":
                Assert.True(
                    line is not null && line.Contains("1 " + noun, StringComparison.Ordinal) && line.Contains("1 NOT READ", StringComparison.Ordinal),
                    $"the {label} line of a post with 2 signed, 1 readable, is: {line ?? "(no line)"}");
                break;
            case "the only item dropped":
                Assert.True(line is not null, $"no {label} line for a post whose only {(refs ? "reference" : "code block")} was dropped");
                Assert.True(line!.Contains("0 " + noun, StringComparison.Ordinal) && line.Contains("1 NOT READ", StringComparison.Ordinal), line);
                break;
            default:
                Assert.Equal(refs ? RefsToday : CodeToday, line);
                break;
        }
    }
}
