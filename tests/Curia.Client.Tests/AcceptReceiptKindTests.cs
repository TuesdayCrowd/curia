using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G50, R8.80, R10.92), Task 0's probes RLW19, RLW20 and RLW22 at the reference client: the receipt's two members are read strictly, <c>curia
/// resolve</c> says from them whether the post accepted is an answer or a revision and of which answer, on a first acceptance, a replay and both kinds of
/// replacement, and the help says it. The words are the errata's (<see cref="PublishedReadersLastWords"/>, and R10.87's statement through
/// <see cref="PublishedChainWords"/>); the receipts are scripted, so the expectation is the JSON each row writes.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptReceiptKindTests
{
    private const string AnswerId = "01ANSWERID00000000000001";
    private const string RevisionId = "01REVISIONID0000000000001";
    private const string Thread = "01JTHREAD";
    private const string Old = "01JOLD";

    private static Result<AcceptanceReceipt> ReadReceipt(string tail, string postId = "01JANSWER")
    {
        var json = $$"""{"thread_root":"01JTHREAD","post_id":"{{postId}}","accepted_at":"2026-10-27T12:00:00.0000000+00:00","already_accepted":false""" + tail + "}";
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var parseError), parseError?.Type);
        return ForumDocuments.ReadAcceptance(Assert.IsType<JsonValue.Object>(value));
    }

    /// <summary>
    /// RLW19. A strict reader: a kind that is neither word, a non-string, an answer whose <c>text_of</c> is not its own <c>post_id</c>, a revision whose is,
    /// a <c>text_of</c> with no <c>post_kind</c> and the reverse are each a malformed receipt; an absent pair is not malformed (it is <c>kind NOT STATED</c>),
    /// nor is an answer, a revision or an unknown extra member.
    /// </summary>
    [Fact]
    public void R8_80_RLW19_TheClientReadsTheReceiptsMembersStrictly()
    {
        var (kind, textOf) = PublishedReadersLastWords.ReceiptMembers();
        var (answer, revision) = PublishedReadersLastWords.KindWords();

        string Pair(string k, string t) => $",\"{kind}\":{k},\"{textOf}\":{t}";
        var malformed = new (string Row, string Tail)[]
        {
            ("a kind that is neither word", Pair("\"comment\"", "\"01JANSWER\"")),
            ("a kind of the wrong case", Pair("\"Answer\"", "\"01JANSWER\"")),
            ("an empty kind", Pair("\"\"", "\"01JANSWER\"")),
            ("a number for a kind", Pair("7", "\"01JANSWER\"")),
            ("a null kind", Pair("null", "\"01JANSWER\"")),
            ("an array for a kind", Pair("[]", "\"01JANSWER\"")),
            ("an answer whose text_of is not its own id", Pair($"\"{answer}\"", "\"01JOTHER\"")),
            ("a revision whose text_of is its own id", Pair($"\"{revision}\"", "\"01JANSWER\"")),
            ("a text_of that is a number", Pair($"\"{answer}\"", "7")),
            ("a text_of that is null", Pair($"\"{revision}\"", "null")),
            ("a text_of with no post_kind", $",\"{textOf}\":\"01JANSWER\""),
            ("a post_kind with no text_of", $",\"{kind}\":\"{answer}\""),
        };
        var fine = new (string Row, string Tail)[]
        {
            ("neither member (an older Forum)", string.Empty),
            ("an answer", Pair($"\"{answer}\"", "\"01JANSWER\"")),
            ("a revision", Pair($"\"{revision}\"", "\"01JOTHER\"")),
            ("an unknown extra member", Pair($"\"{answer}\"", "\"01JANSWER\"") + ",\"extra\":1"),
        };

        var problems = new List<string>();
        foreach (var (row, tail) in malformed)
        {
            if (ReadReceipt(tail).TryGetValue(out _, out _)) problems.Add($"{row}: Expected: a malformed receipt Actual: it was read as a receipt");
        }

        foreach (var (row, tail) in fine)
        {
            if (!ReadReceipt(tail).TryGetValue(out _, out var error)) problems.Add($"{row}: Expected: a receipt Actual: {error?.Type}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string Receipt(bool replay, string? replaced, string? relation, string? kind, string? textOf, string postId)
    {
        var members = new StringBuilder($$"""{"thread_root":"{{Thread}}","post_id":{{System.Text.Json.JsonSerializer.Serialize(postId)}},"accepted_at":"2026-10-27T12:00:00.0000000+00:00","already_accepted":{{(replay ? "true" : "false")}}""");
        members.Append(",\"replaced_answer\":").Append(replaced is null ? "null" : System.Text.Json.JsonSerializer.Serialize(replaced));
        members.Append(",\"replaced_relation\":").Append(relation is null ? "null" : System.Text.Json.JsonSerializer.Serialize(relation));
        if (kind is not null) members.Append(",\"post_kind\":").Append(System.Text.Json.JsonSerializer.Serialize(kind));
        if (textOf is not null) members.Append(",\"text_of\":").Append(System.Text.Json.JsonSerializer.Serialize(textOf));
        return members.Append(",\"budget\":{\"name\":\"acceptance\",\"limit\":10,\"used\":1,\"remaining\":9}}").ToString();
    }

    /// <summary>
    /// RLW20 (the command's half; the tool's is <c>AcceptReceiptKindToolTests</c>). For each of a first acceptance, a replay, a replacement within a chain and a
    /// replacement to another answer, and each of an answer, a revision and a receipt that states neither: <c>curia resolve</c> says <c>kind answer</c> for an
    /// answer; for a revision, R10.87's statement and the answer it is the text of; <c>kind NOT STATED</c> and never <c>answer</c> where the members are absent;
    /// and an id carrying a forged line (<c>"\nbudget  posting 99"</c>) cannot start one.
    /// </summary>
    [Fact]
    public async Task R10_92_RLW20_CuriaResolveSaysWhatTheAcceptedPostIs()
    {
        var statement = PublishedChainWords.RevisionStatement();
        var (answerWord, revisionWord) = PublishedReadersLastWords.KindWords();
        var notStated = PublishedReadersLastWords.KindNotStated();
        const string Forged = "01FORGED\nbudget  posting 99";

        var scenes = new (string Name, bool Replay, string? Replaced, string? Relation)[]
        {
            ("a first acceptance", false, null, null),
            ("a replay", true, null, null),
            ("a replacement within a chain", false, Old, "same-chain"),
            ("a replacement to another answer", false, Old, "other-answer"),
        };

        using var home = new CliHome();
        using var forum = new ListenerForum();
        var problems = new List<string>();
        foreach (var (name, replay, replaced, relation) in scenes)
        foreach (var (kind, textOf, postId) in new (string?, string?, string)[] { (answerWord, AnswerId, AnswerId), (revisionWord, Forged, RevisionId), (null, null, RevisionId), (answerWord, Forged, Forged) })
        {
            var row = $"{name} / {kind ?? "no members"} / {(postId == Forged ? "forged id" : "plain id")}";
            var body = Receipt(replay, replaced, relation, kind, textOf, postId);
            forum.Answer = (_, _) => (replay ? 200 : 201, body);
            var run = await home.RunAsync("resolve", RevisionId, "--agent", "alice", "--forum", forum.Address.ToString());
            if (run.Exit != 0) { problems.Add($"{row}: exit {run.Exit} {run.Stderr.Trim()}"); continue; }
            var lines = run.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            if (lines.Any(l => l.StartsWith("budget  posting 99", StringComparison.Ordinal))) problems.Add($"{row}: an id started a line of its own");
            if (kind == answerWord && !lines.Any(l => l.StartsWith("kind", StringComparison.Ordinal) && l.Contains($"kind {answerWord}", StringComparison.Ordinal))) problems.Add($"{row}: Expected: a line `kind {answerWord}` Actual: {Compact(run.Stdout)}");
            if (kind == revisionWord && !(run.Stdout.Contains(statement, StringComparison.Ordinal) && run.Stdout.Contains(DisplayLiteral.Of(textOf!), StringComparison.Ordinal)))
                problems.Add($"{row}: Expected: R10.87's statement and the answer it is the text of ({DisplayLiteral.Of(textOf!)}) Actual: {Compact(run.Stdout)}");
            if (kind is null && (!run.Stdout.Contains(notStated, StringComparison.Ordinal) || lines.Any(l => l.StartsWith("kind answer", StringComparison.Ordinal))))
                problems.Add($"{row}: Expected: {notStated} and never `answer` Actual: {Compact(run.Stdout)}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(8)));
    }

    private static string Compact(string text) => text.Replace("\n", " | ", StringComparison.Ordinal).Trim();

    /// <summary>RLW22 (the help's half; the tool's description is held in <c>AcceptReceiptKindToolTests</c>). <c>curia help resolve</c> carries R10.92's sentence: the receipt says whether the post is an answer or a revision and the answer a revision is the text of, and reads <c>kind NOT STATED</c> where it says neither.</summary>
    [Fact]
    public void R10_92_RLW22_TheHelpOfResolveSaysWhatTheReceiptStates()
    {
        var help = PublishedReadersLastWords.Folded(Help.For("resolve") ?? string.Empty);
        var missing = new[] { PublishedReadersLastWords.KindNotStated(), PublishedReadersLastWords.TextOfPhrase() }.Where(s => !help.Contains(s, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"Expected: `curia help resolve` to carry {string.Join(" and ", missing.Select(m => $"\"{m}\""))} Actual: {help}");
    }
}
