using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// WRD1m and WRD10 (errata G77, R11.105, R11.96 words (the tool) (rev.), register item 327): <c>curia_keys</c>'s text begins with the line the errata publishes, which is the one line of the tool that moves, and its
/// description is the one the errata publishes, unchanged. The words are read from the errata at test time. Written against the unmodified tree; the version bump is held by VER1 and the probes that read it by the one parser.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyPagesWordsToolTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-wrd-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        return await McpProtocolSession.StartAsync(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer), writer.Agent.Profile.AgentId, agent);
    }

    /// <summary>The backtick spans of the section of <paramref name="paragraph"/> after the italic <paramref name="label"/>, up to the next italic label that begins with a capital.</summary>
    private static string[] Spans(string paragraph, string label)
    {
        var text = PublishedAcceptBudget.Paragraph(paragraph);
        var marker = $"*{label}:*";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException($"the errata's {paragraph} do not hold {marker} exactly once");

        var start = at + marker.Length;
        var next = Regex.Match(text[start..], @"\*[A-Z][^*`]*:\*", RegexOptions.CultureInvariant);
        var section = next.Success ? text[start..(start + next.Index)] : text[start..];
        return [.. Regex.Matches(section, "`([^`]+)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value)];
    }

    /// <summary>
    /// WRD1m: the first line of <c>curia_keys</c>'s text is the errata's line, by equality, over the stub's unremarkable key set: the tool's text is the CLI's (one definition), and the line it begins with is the one
    /// item 327's readers met. Red on the tree: <c>... checked by curia verify, anchors a key's binding</c> (no form).
    /// </summary>
    [Fact]
    public async Task WRD1m_TheFirstLineOfCuriaKeysIsTheErratas()
    {
        var published = Spans("**R11.105 words.**", "The first line").Single();

        await using var session = await StartAsync();
        var answer = await session.CallAsync("curia_keys", []);
        var first = answer.Text.Split('\n')[0].TrimEnd('\r');

        WholeText.Same($"curia_keys succeeded; first line: {published}", $"curia_keys {(answer.IsError ? "failed" : "succeeded")}; first line: {first}");
    }

    /// <summary>
    /// WRD10 (control): the registered description of <c>curia_keys</c> is, whole, what the errata publishes: R11.96 words (the tool) (rev.)'s description, then the one sentence of R11.100 words and the one of R11.101
    /// words, joined as the catalogue joins them, and then the tier span the catalogue ends every description with. G77 moves the first line of the tool's text and not its description (R11.96 words (the tool) (rev.) says the first line in other words and stays true), so the
    /// description must not drift when the line does. Green on the tree; the existing R11_96 probe holds only that the description begins with the published one.
    /// </summary>
    [Fact]
    public async Task WRD10_TheDescriptionOfCuriaKeysIsWhatTheErrataPublishes_Control()
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R11.96 words (the tool) (rev.)**");
        const string Label = "*The description:* ``";
        var start = paragraph.IndexOf(Label, StringComparison.Ordinal) + Label.Length;
        var published = paragraph[start..paragraph.IndexOf("``", start, StringComparison.Ordinal)].Replace("\\n", "\n", StringComparison.Ordinal);
        var settling = Spans("**R11.100 words.**", "The description's added sentence").Single();
        var standing = Spans("**R11.101 words.**", "The description sentence of curia_keys").Single();

        await using var session = await StartAsync();
        var description = (await session.ListAsync()).Single(t => t.Name == "curia_keys").Description ?? string.Empty;

        // The catalogue ends every description with its tier span, composed from Table 10 (here "Requires no credential: ..."); the errata publishes the rest.
        var expected = published + "\n\n" + settling + " " + standing;
        var tail = description.Length >= expected.Length ? description[expected.Length..] : string.Empty;
        WholeText.Same(
            expected + "\n\nRequires no credential",
            (description.StartsWith(expected, StringComparison.Ordinal) ? expected : description) + (tail.StartsWith("\n\nRequires no credential", StringComparison.Ordinal) ? "\n\nRequires no credential" : "\n\n<" + tail + ">"));
    }
}
