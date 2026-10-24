using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Domain.Tests.Content;

/// <summary>
/// Errata G28, R8.66: an item of <c>refs</c> or <c>code_blocks</c> the envelope reader cannot read is counted, not lost.
///
/// <para><b>Where the expectation comes from.</b> Not from <see cref="PostEnvelope"/>: the number the author signed is
/// counted by a second parser, <c>System.Text.Json</c>, over the canonical bytes of the very envelope the reader is given
/// (an array's length; a member present and not an array counts as one, R8.66's sentence; an absent member none). The
/// envelope is Appendix C.3's, parsed from the white paper at test time and made an answer, with one member replaced.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DroppedItemsTests
{
    private const string WhitePaper = "curia-agent-forum-WHITEPAPER.md";
    private const string Heading = "### C.3 Content envelope and detached signature";
    private const string ParentId = "01K2F8Q9X3B7NQ4V2H6ZTC1M8D";

    private const string GoodRef = """{"kind":"url","target":"https://a.example/x"}""";
    private const string GoodCode = """{"language":"csharp","source":"var x = 1;"}""";

    /// <summary>Each row: the member, a name, the member's raw JSON (null: absent) and whether the row is a control that drops nothing.</summary>
    public static TheoryData<string, string, string?, bool> Rows()
    {
        var data = new TheoryData<string, string, string?, bool>();
        foreach (var (member, good, spelledWrong) in new[]
        {
            ("refs", GoodRef, """{"kind":"url","value":"https://b.example/y"}"""),
            ("code_blocks", GoodCode, """{"language":"csharp","text":"var y = 2;"}"""),
        })
        {
            data.Add(member, "all readable", $"[{good},{good}]", true);
            data.Add(member, "one spelled wrong", $"[{good},{spelledWrong}]", false);
            data.Add(member, "one not an object", $"[{good},\"text\"]", false);
            data.Add(member, "all dropped", $"[{spelledWrong},{spelledWrong}]", false);
            data.Add(member, "member a string", "\"not an array\"", false);
            data.Add(member, "member an object", good, false);
            data.Add(member, "member absent", null, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void R8_66_AnItemTheReaderCannotReadIsCountedNotLost(string member, string row, string? raw, bool control)
    {
        var envelope = With(AsAnAnswer(PublishedEnvelope()), member, raw);
        var signed = SignedCount(envelope, member);
        Assert.Equal(raw is null ? 0 : raw.StartsWith('[') ? JsonDocument.Parse(raw).RootElement.GetArrayLength() : 1, signed);

        Assert.True(PostEnvelope.Read(envelope).TryGetValue(out var read, out var error), error?.Type);
        var (kept, dropped) = member == "refs" ? (read!.Refs.Length, read.DroppedRefs) : (read!.CodeBlocks.Length, read.DroppedCodeBlocks);

        Assert.True(
            kept + dropped == signed,
            $"{member}, {row}: the author signed {signed}, the reader kept {kept} and counted {dropped} dropped");
        if (control) Assert.Equal(0, dropped);
        else Assert.True(dropped > 0, $"{member}, {row}: nothing was dropped, so the row exercises nothing; a defect in this fact");
    }

    /// <summary>The number the author signed, by a parser that is not <see cref="PostEnvelope"/>, over the envelope's canonical bytes.</summary>
    private static int SignedCount(JsonValue.Object envelope, string member)
    {
        Assert.True(CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out _));
        using var document = JsonDocument.Parse(canonical.ToArray());
        if (!document.RootElement.TryGetProperty(member, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 1;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, WhitePaper)))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"{WhitePaper} not found above {AppContext.BaseDirectory}");
    }

    private static JsonValue.Object Parse(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default);
        Assert.True(parsed.TryGetValue(out var tree, out var error), error?.Type);
        return (JsonValue.Object)tree!;
    }

    /// <summary>C.3's <c>envelope</c> object, read out of the first fenced block after its heading.</summary>
    private static JsonValue.Object PublishedEnvelope()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), WhitePaper));
        var heading = text.IndexOf(Heading, StringComparison.Ordinal);
        Assert.True(heading >= 0, $"{WhitePaper} no longer contains '{Heading}'");
        var open = text.IndexOf("```json", heading, StringComparison.Ordinal);
        var start = text.IndexOf('\n', open) + 1;
        var end = text.IndexOf("```", start, StringComparison.Ordinal);
        return (JsonValue.Object)Parse(text[start..end]).Members.Single(m => m.Key == "envelope").Value;
    }

    private static JsonValue.Object AsAnAnswer(JsonValue.Object source) => new(
        [.. source.Members
            .Where(m => m.Key is not ("kind" or "parent" or "prev" or "title"))
            .Append(new("kind", new JsonValue.String("answer")))
            .Append(new("parent", new JsonValue.String(ParentId)))]);

    /// <summary>The envelope with <paramref name="member"/> replaced by the raw JSON given, or removed when it is null.</summary>
    private static JsonValue.Object With(JsonValue.Object source, string member, string? raw)
    {
        var members = source.Members.Where(m => m.Key != member).ToImmutableArray();
        if (raw is not null) members = members.Add(new(member, Parse("{\"m\":" + raw + "}").Members.Single().Value));
        return new JsonValue.Object(members);
    }
}
