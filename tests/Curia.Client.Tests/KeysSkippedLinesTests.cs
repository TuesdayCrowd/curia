using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// LRV26 (errata G68, R4.41, ruling 30): the client's parse of <c>curia_skipped</c> and <c>curia_skipped_total</c>, strict as <c>curia_lifecycle</c> is. A member that is not what it says is a malformed response
/// naming it; a document with neither parses to <c>Skipped = null</c>, which means the Forum does not say. Red on the seam: <c>Expected: Skipped = [2 rows]  Actual: null</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeysSkippedLinesTests
{
    private static Result<KeySetDocument> Parse(string json) =>
        ForumDocuments.ReadKeySet(JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default).Match(v => v, e => throw new InvalidOperationException(e.Type)));

    private static string Row(string logIndex = "2", string type = "agent.key-retired", string predicate = "statement-mismatch") =>
        $"{{\"log_index\":{logIndex},\"type\":\"{type}\",\"predicate\":\"{predicate}\"}}";

    private static string Document(string members) => $"{{\"keys\":[],\"curia_lifecycle\":[0,1],{members}}}";

    private static string Show(Result<KeySetDocument> result) =>
        result.Match(
            d => d.Skipped is { } rows ? $"Skipped = [{rows.Length} rows], total {d.SkippedTotal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}" : "Skipped = null",
            e => $"refused {e.Type} {e.Detail}");

    [Fact]
    public void LRV26_AWellFormedListParses()
    {
        var parsed = Parse(Document($"\"curia_skipped\":[{Row("2")},{Row("5", "agent.key-bound", "broken-link")}],\"curia_skipped_total\":2"));

        WholeText.Same("Skipped = [2 rows], total 2", Show(parsed));
    }

    [Theory]
    [InlineData("\"curia_skipped\":[{\"log_index\":\"2\",\"type\":\"agent.key-retired\",\"predicate\":\"statement-mismatch\"}],\"curia_skipped_total\":1", "a log_index that is not an integer")]
    [InlineData("\"curia_skipped\":[{\"log_index\":2,\"type\":\"agent.key-retired\"}],\"curia_skipped_total\":1", "a missing member")]
    [InlineData("\"curia_skipped\":[{\"log_index\":2,\"type\":\"agent.key-retired\",\"predicate\":\"statement-mismatch\",\"payload\":{}}],\"curia_skipped_total\":1", "an extra member")]
    [InlineData("\"curia_skipped\":[{\"log_index\":2,\"type\":\"agent.key-retired\",\"predicate\":\"statement-mismatch\"}],\"curia_skipped_total\":0", "a total below the list")]
    [InlineData("\"curia_skipped\":\"x\",\"curia_skipped_total\":1", "a list that is not an array")]
    public void LRV26_AMemberThatIsNotWhatItSaysIsAMalformedResponseNamingIt(string members, string why)
    {
        var parsed = Parse(Document(members));

        Assert.True(
            parsed.Match(_ => false, e => e.Detail is not null && e.Detail.Contains("curia_skipped", StringComparison.Ordinal)),
            $"Expected: a malformed response naming curia_skipped ({why})  Actual: {Show(parsed)}");
    }

    [Fact]
    public void LRV26_MoreThanTwentyRowsAreMalformed()
    {
        var rows = string.Join(",", Enumerable.Range(0, 21).Select(i => Row(i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var parsed = Parse(Document($"\"curia_skipped\":[{rows}],\"curia_skipped_total\":21"));

        Assert.True(
            parsed.Match(_ => false, e => e.Detail is not null && e.Detail.Contains("curia_skipped", StringComparison.Ordinal)),
            $"Expected: a malformed response naming curia_skipped (a list of 21)  Actual: {Show(parsed)}");
    }

    /// <summary>A document with neither member parses to <c>Skipped = null</c>: the Forum does not say (a control on the seam).</summary>
    [Fact]
    public void LRV26_ADocumentWithNeitherMemberDoesNotSay_Control()
    {
        WholeText.Same("Skipped = null", Show(Parse(Document("\"curia_x\":1"))));
    }
}

/// <summary>
/// LRV27 (errata G68, R11.96): <c>curia keys</c> prints a <c>SKIPPED</c> line for each row the key set names, in the errata's words (parsed from the errata, never typed), and exits 6 while any is named; a row at an
/// index the profile recorded prints the second form and replaces the <c>MISSING</c> line that index would print; a hostile type or predicate is a display literal; a key set without the members says so, and the exit is
/// what the rest gives. Driven in process through <c>Program.ShowKeysAsync</c>, as <see cref="KeyActVerbTests"/> drive it. Red on the seam: <c>Expected: SKIPPED line  Actual: no such line</c>.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeysCommandSkippedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WithSkipped(string keySet, string rows, int total) =>
        keySet[..^1] + $",\"curia_skipped\":[{rows}],\"curia_skipped_total\":{total.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    private static string Row(long index, string type, string predicate) =>
        $"{{\"log_index\":{index.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"type\":{System.Text.Json.JsonSerializer.Serialize(type)},\"predicate\":{System.Text.Json.JsonSerializer.Serialize(predicate)}}}";

    private static string Fill(string template, string type, long index, string predicate) =>
        template.Replace("<type>", type, StringComparison.Ordinal).Replace("<i>", index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("<predicate>", predicate, StringComparison.Ordinal);

    private static async Task<(int Exit, string Stdout)> KeysAsync(KeyActRig rig)
    {
        var (exit, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));
        return (exit, stdout);
    }

    private static string Lines(string stdout, string needle) =>
        string.Join(" | ", stdout.Split('\n').Where(l => l.Contains(needle, StringComparison.Ordinal)).Select(l => l.Trim()));

    /// <summary>(a) a row the profile did not make prints the first words and the exit is 6.</summary>
    [Fact]
    public async Task LRV27_ARowTheProfileDidNotMakePrintsTheFirstWordsAndExitsSix()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = WithSkipped(KeyActDouble.KeySetJson([new KeyActDouble.Key("a-0000abcd", 1)], [0, 1]), Row(5, "agent.key-retired", "statement-mismatch"), 1);

        var (exit, stdout) = await KeysAsync(rig);
        var words = Fill(PublishedClientWords.SkippedNotMade, "agent.key-retired", 5, "statement-mismatch");

        WholeText.Same("exit 6; the first words", $"exit {exit}; {(PublishedClientWords.Says(stdout, words) ? "the first words" : "no such line: " + Lines(stdout, "SKIPPED"))}");
    }

    /// <summary>(b) a row at an index the profile recorded prints the second words and replaces the MISSING line for that index: one line, not two.</summary>
    [Fact]
    public async Task LRV27_ARowAtAnIndexTheProfileRecordedPrintsTheSecondWordsInPlaceOfMissing()
    {
        using var rig = KeyActRig.Create();
        var (rotated, _, error) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));
        Assert.True(rotated == 0, error);
        var at = rig.Double.AcceptedAt;
        rig.Reload();
        rig.Double.KeySet = WithSkipped(KeyActDouble.KeySetJson([new KeyActDouble.Key("a-0000abcd", 1)], [0, 1]), Row(at, "agent.key-bound", "broken-link"), 1);

        var (exit, stdout) = await KeysAsync(rig);
        var words = Fill(PublishedClientWords.SkippedMade, "agent.key-bound", at, "broken-link");
        var atIndex = stdout.Split('\n').Count(l => l.Contains($"log index {at.ToString(System.Globalization.CultureInfo.InvariantCulture)} ", StringComparison.Ordinal));

        WholeText.Same(
            "exit 6; the second words; 1 line at the index; no MISSING",
            $"exit {exit}; {(PublishedClientWords.Says(stdout, words) ? "the second words" : "no such line: " + Lines(stdout, "SKIPPED"))}; {atIndex} line(s) at the index; "
            + (stdout.Contains("MISSING", StringComparison.Ordinal) ? "MISSING printed: " + Lines(stdout, "MISSING") : "no MISSING"));
    }

    /// <summary>(c) a hostile type or predicate is written as a display literal.</summary>
    [Fact]
    public async Task LRV27_AHostileTypeOrPredicateIsADisplayLiteral()
    {
        using var rig = KeyActRig.Create();
        const string type = "agent.key-retired\nSKIPPED: forged";
        const string predicate = "x\"y";
        rig.Double.KeySet = WithSkipped(KeyActDouble.KeySetJson([new KeyActDouble.Key("a-0000abcd", 1)], [0, 1]), Row(5, type, predicate), 1);

        var (_, stdout) = await KeysAsync(rig);
        var words = Fill(PublishedClientWords.SkippedNotMade, Curia.Canon.Json.DisplayLiteral.Of(type), 5, Curia.Canon.Json.DisplayLiteral.Of(predicate));

        WholeText.Same("the words with display literals; no raw newline line", $"{(PublishedClientWords.Says(stdout, words) ? "the words with display literals" : "no such line: " + Lines(stdout, "SKIPPED"))}; {(stdout.Contains("\nSKIPPED: forged", StringComparison.Ordinal) ? "a raw forged line" : "no raw newline line")}");
    }

    /// <summary>(d) a key set without the members says it does not say, and the exit is what the rest gives (0 on a clean comparison).</summary>
    [Fact]
    public async Task LRV27_AKeySetWithoutTheMembersSaysSo()
    {
        using var rig = KeyActRig.Create();

        var (exit, stdout) = await KeysAsync(rig);

        WholeText.Same("exit 0; the no-members words", $"exit {exit}; {(PublishedClientWords.Says(stdout, PublishedClientWords.SkippedNoMembers) ? "the no-members words" : "no such line")}");
    }

    /// <summary>(e) more rows than the list prints: the list's lines, then how many more.</summary>
    [Fact]
    public async Task LRV27_RowsPastTheListSayHowManyMore()
    {
        using var rig = KeyActRig.Create();
        var rows = string.Join(",", Enumerable.Range(0, 20).Select(i => Row(10 + i, "agent.key-retired", "statement-mismatch")));
        rig.Double.KeySet = WithSkipped(KeyActDouble.KeySetJson([new KeyActDouble.Key("a-0000abcd", 1)], [0, 1]), rows, 25);

        var (exit, stdout) = await KeysAsync(rig);
        var more = PublishedClientWords.SkippedPastList.Replace("<n>", "5", StringComparison.Ordinal);

        WholeText.Same(
            "exit 6; 20 SKIPPED lines; and 5 more",
            $"exit {exit}; {stdout.Split('\n').Count(l => l.TrimStart().StartsWith("SKIPPED:", StringComparison.Ordinal))} SKIPPED lines; {(PublishedClientWords.Says(stdout, more) ? "and 5 more" : "no more line")}");
    }
}

/// <summary>
/// LRV29: the readers of a post do not read the list (errata G68, ruling 30). <c>curia verify</c> and <c>curia read</c> over two key-set documents that differ only in the two members produce byte-identical output and
/// exit codes. Green on the seam by design (a control): it is the probe the falsification of the readers turns red.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReadersIgnoreTheSkippedListTests : IDisposable
{
    private readonly List<string> _homes = [];
    private readonly List<LifecycleCaseForum> _forums = [];

    public void Dispose()
    {
        foreach (var forum in _forums) forum.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LRV29_VerifyAndTheSignatureReadAreByteIdenticalWithAndWithoutTheMembers_Control()
    {
        var directory = Path.Combine(LifecycleConformanceTests.Root, "rotation-then-post");
        var plain = await RenderAsync(directory, withMembers: false);
        var listed = await RenderAsync(directory, withMembers: true);

        WholeText.Same(plain, listed);
    }

    private async Task<string> RenderAsync(string directory, bool withMembers)
    {
        var forum = new LifecycleCaseForum(directory) { SkippedMembers = withMembers ? "\"curia_skipped\":[{\"log_index\":9,\"type\":\"agent.key-retired\",\"predicate\":\"statement-mismatch\"},{\"log_index\":10,\"type\":\"agent.key-bound\",\"predicate\":\"broken-link\"},{\"log_index\":11,\"type\":\"agent.key-retired\",\"predicate\":\"lifecycle-typ-mismatch\"}],\"curia_skipped_total\":3" : null };
        _forums.Add(forum);
        var home = Directory.CreateTempSubdirectory("curia-lrv29-").FullName;
        _homes.Add(home);
        var post = forum.Post();
        var verifier = new PostVerifier(forum.Client(), new HeadStore(home));
        var verified = await verifier.VerifyAsync(post, TestContext.Current.CancellationToken);
        var keys = await forum.Client().GetKeySetAsync(forum.Author, TestContext.Current.CancellationToken);
        var signature = keys.TryGetValue(out var set, out _) ? SignatureCheck.Verify(post, set!.Keys).Detail : "key set refused";
        return $"{verified.Render()}\nexit {ExitCode.ForOutcomes(verified.Overall)}\nread: {signature}";
    }
}
