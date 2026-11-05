using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Domain.Tests.Content;

/// <summary>
/// R8.87 (errata G61), RPB1 and RPB2, the pure halves: <see cref="ParentPolicy.ReplyBoard"/> as a function of the kind, the submitted board, the followed post and the
/// board stored for it; and <see cref="PostEnvelope.AdmissionRefusal"/> as a function of the kind, which asks R8.85's grammar of every kind but an answer, a comment and a
/// revision.
///
/// <para><b>Oracles.</b> The type, the title and the detail keys are parsed from R8.87 words (<see cref="PublishedReplyBoardWords"/>) and the grammar's type and ten classes from
/// R8.85 words (<see cref="PublishedBoardWords"/>), never from the constants. The kinds are Table 9's seven, spelled out here, so a kind added to the code does not silently join
/// the theory (the count is asserted). Each row names its own expected answer; none is computed by the comparison under test. On the seam <c>ReplyBoard</c> returns none, so the
/// rows that must refuse are red by assertion, and <c>AdmissionRefusal</c> still asks the grammar of a reply, so RPB2's reply rows are red.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReplyBoardPolicyTests
{
    private const string Followed = "01M5VAMSJ17A9JBE3R41RAHMWV";

    private static readonly string[] Table9Kinds = ["question", "answer", "finding", "comment", "revision", "vote", "verification"];

    private static PostKind Kind(string wire)
    {
        Assert.True(PostKinds.TryParse(wire, out var kind), $"{wire} is not a kind the code parses");
        return kind;
    }

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Word(Curia.Domain.Primitives.Error? error) => error is null ? "none" : $"{error.Type} {error.Detail}";

    /// <summary>The same letters in two spellings: e with an acute accent as one scalar (NFC) and as e and a combining accent (NFD).</summary>
    private static readonly string Composed = "caf" + char.ConvertFromUtf32(0xE9);

    private static readonly string Decomposed = "cafe" + char.ConvertFromUtf32(0x301);

    /// <summary>
    /// RPB1. The comparison, as a table. For an answer, a comment and a revision: an equal board is none, and any difference is the published refusal naming the followed post
    /// (<c>parent=</c> for an answer and a comment, <c>original=</c> for a revision) and no board: a case difference, a trailing space, the same letters in NFC and in NFD, an empty
    /// board against a board. For the four kinds that follow no post the answer is none whatever the two boards are. Predicted red: the seam returns none for the unequal rows.
    /// </summary>
    [Fact]
    public void R8_87_RPB1_AReplyIsRefusedUnlessItsBoardIsTheFollowedPostsAndNoOtherKindIsAsked()
    {
        Assert.Equal(7, Table9Kinds.Length);
        Assert.Equal(7, Enum.GetValues<PostKind>().Length);
        var (parentKey, originalKey) = PublishedReplyBoardWords.DetailKeys();
        var type = PublishedReplyBoardWords.Type();

        // (submitted, followed, differs): every row is a pair of strings chosen here.
        (string Submitted, string Followed, bool Differs)[] pairs =
        [
            ("rust", "rust", false),
            ("Meta", "Meta", false),
            (Composed, Composed, false),
            ("rust", "dotnet", true),
            ("meta", "Meta", true),
            ("Meta", "meta", true),
            ("rust ", "rust", true),
            ("rust", "rust ", true),
            (Composed, Decomposed, true),
            (Decomposed, Composed, true),
            (string.Empty, "rust", true),
            ("rust", string.Empty, true),
        ];

        var problems = new List<string>();
        var refused = 0;
        var accepted = 0;
        foreach (var wire in new[] { "answer", "comment", "revision" })
        {
            var key = wire == "revision" ? originalKey : parentKey;
            foreach (var (submitted, followed, differs) in pairs)
            {
                var error = ParentPolicy.ReplyBoard(Kind(wire), submitted, Followed, followed);
                if (!differs)
                {
                    accepted++;
                    if (error is not null)
                        problems.Add($"{wire} {Escape(submitted)} under {Escape(followed)}: Expected: none Actual: {Word(error)}");
                    continue;
                }

                refused++;
                var expected = $"{type} {key}{Followed}";
                if (!string.Equals(Word(error), expected, StringComparison.Ordinal))
                    problems.Add($"{wire} {Escape(submitted)} under {Escape(followed)}: Expected: {expected} Actual: {Word(error)}");
                else if (error!.Title != PublishedReplyBoardWords.Title())
                    problems.Add($"{wire}: Expected: title {PublishedReplyBoardWords.Title()} Actual: {error.Title}");
                else if (submitted.Length > 3 && error.Detail!.Contains(submitted, StringComparison.Ordinal))
                    problems.Add($"{wire}: the refusal echoes the submitted board");
            }
        }

        // The kinds that follow no post are not asked, whatever the two boards are.
        foreach (var wire in new[] { "question", "finding", "vote", "verification" })
            foreach (var (submitted, followed, _) in pairs)
                if (ParentPolicy.ReplyBoard(Kind(wire), submitted, Followed, followed) is { } asked)
                    problems.Add($"{wire} {Escape(submitted)} under {Escape(followed)}: Expected: none Actual: {Word(asked)}");

        // Reached: the table held both classes of row for each of the three kinds that follow a post.
        Assert.True(refused == 3 * pairs.Count(p => p.Differs) && accepted == 3 * pairs.Count(p => !p.Differs), "NOT REACHED: the table lost a class of row");
        Assert.True(problems.Count == 0, "Expected: the published refusal for every unequal pair of an answer, a comment and a revision, and none for every other row" + Environment.NewLine + "Actual:   " + string.Join("; ", problems));
    }

    private static string Escape(string text) => DisplayLiteral.Of(text);

    // ---- RPB2 -----------------------------------------------------------------------------------

    private static KeyValuePair<string, JsonValue> M(string name, JsonValue value) => new(name, value);

    /// <summary>A valid envelope of the kind on <paramref name="board"/>, with the members its obligations ask for.</summary>
    private static PostEnvelope EnvelopeOf(PostKind kind, string board)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            M("v", new JsonValue.Number(1)),
            M("kind", new JsonValue.String(PostKinds.Wire(kind))),
            M("author", new JsonValue.String("https://agents.example/bob")),
            M("board", new JsonValue.String(board)),
            M("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            M("created_at", new JsonValue.String("2026-09-04T12:00:00Z")),
            M("nonce", new JsonValue.String("n")),
        };
        if (PostKinds.RequiresBody(kind)) members.Add(M("body", new JsonValue.String("some body")));
        if (PostKinds.RequiresTitle(kind)) members.Add(M("title", new JsonValue.String("A title")));
        if (PostKinds.RequiresParent(kind)) members.Add(M("parent", new JsonValue.String("01P")));
        if (PostKinds.RequiresPrev(kind)) members.Add(M("prev", new JsonValue.String("sha256:" + new string('b', 64))));
        if (PostKinds.RequiresTarget(kind)) members.Add(M("target", new JsonValue.String("sha256:" + new string('a', 64))));
        if (kind is PostKind.Vote)
        {
            members.Add(M("endorse", new JsonValue.Bool(true)));
            members.Add(M("predicted_endorsement_bp", new JsonValue.Number(5000)));
            members.Add(M("epoch", new JsonValue.Number(1)));
        }

        if (kind is PostKind.Verification)
        {
            members.Add(M("method", new JsonValue.String("ran it")));
            members.Add(M("result", new JsonValue.String("reproduced")));
            members.Add(M("refs", new JsonValue.Array([
                new JsonValue.Object([M("kind", new JsonValue.String("url")), M("target", new JsonValue.String("https://example.test/t"))])])));
        }

        var read = PostEnvelope.Read(new JsonValue.Object([.. members]));
        Assert.True(read.TryGetValue(out var envelope, out var error), error?.Type);
        return envelope!;
    }

    /// <summary>
    /// RPB2. <c>AdmissionRefusal</c> is asked of the kind: an answer, a comment and a revision on <c>Meta</c> are none (R8.87 judges them, against the followed post's board); a
    /// question, a finding, a vote and a verification on <c>Meta</c> are <c>board-not-canonical</c>, and the ten classes are unchanged (a board failing one class is refused with that
    /// class in the detail). Predicted red for the three reply rows: the seam still asks the grammar and answers <c>board-not-canonical</c>.
    /// </summary>
    [Fact]
    public void R8_87_RPB2_TheGrammarIsNotAskedOfAReplyAndIsAskedOfEveryOtherKind()
    {
        var type = PublishedBoardWords.BoardType();
        var problems = new List<string>();

        foreach (var wire in new[] { "answer", "comment", "revision" })
            if (PostEnvelope.AdmissionRefusal(EnvelopeOf(Kind(wire), "Meta")) is { } refusal)
                problems.Add($"{wire} on Meta: Expected: none Actual: {Word(refusal)}");

        foreach (var wire in new[] { "question", "finding", "vote", "verification" })
        {
            var refusal = PostEnvelope.AdmissionRefusal(EnvelopeOf(Kind(wire), "Meta"));
            if (refusal is null || refusal.Type != type || refusal.Detail != "board: not-lower-case")
                problems.Add($"{wire} on Meta: Expected: {type} board: not-lower-case Actual: {Word(refusal)}");
        }

        // The classes stay as published: a board failing exactly one class, asked of a question, names that class.
        var classes = PublishedBoardWords.Classes();
        Assert.Equal(10, classes.Length);
        (string Class, string Board)[] failing =
        [
            ("separator", "a b"),
            ("control", "a" + (char)10 + "b"),
            ("reserved", "a,b"),
            ("dot-segment", "."),
            ("not-lower-case", "Meta"),
            ("length", new string('a', 65)),
        ];
        foreach (var (cls, board) in failing)
        {
            Assert.Contains(cls, classes);
            var refusal = PostEnvelope.AdmissionRefusal(EnvelopeOf(PostKind.Question, board));
            if (refusal is null || refusal.Type != type || refusal.Detail != "board: " + cls)
                problems.Add($"question on a board failing {cls}: Expected: {type} board: {cls} Actual: {Word(refusal)}");
        }

        Assert.True(problems.Count == 0, "Expected: no grammar for the three replies and the grammar, with its ten classes, for the other four kinds" + Environment.NewLine + "Actual:   " + string.Join("; ", problems));
    }
}
