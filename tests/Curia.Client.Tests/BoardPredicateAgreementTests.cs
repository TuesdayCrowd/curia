using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R8.85 (errata G56), NAM8: the predicate the write side applies to a submission's board
/// (<see cref="PostEnvelope.AdmissionRefusal"/>) is the one the client applies to what it is about to sign (<see cref="SubmissionBuilder.Build"/>, which
/// calls the same function on the envelope as typed). A board admitted at one end and refused at the other is the defect. The shape is
/// <see cref="TagPredicateAgreementTests"/>'s.
///
/// <para><b>Oracles.</b> The spellings and their verdicts are NAM1's table (<see cref="BoardSpellings"/>, written by hand), and both ends are compared
/// with the table and not with each other alone. Two rows of the table are not reachable by an envelope and are skipped by name: the empty board
/// (<c>PostEnvelope.Read</c> refuses it first, <c>missing-or-invalid-field</c>) and a board <see cref="BoardName"/> refuses (a lone surrogate, R11.33's
/// <c>unstorable-member</c>, which the board clause is guarded against). <c>not-nfc</c> is asserted from the <b>typed</b> envelope, which is what
/// <c>Build</c> reads; the route reads the canonical form, where NFC has already been applied (the Api companion holds that by SQL).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BoardPredicateAgreementTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-board-agree-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static KeyValuePair<string, JsonValue> M(string name, JsonValue value) => new(name, value);

    private static PostEnvelope QuestionOn(string board)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            M("v", new JsonValue.Number(1)),
            M("kind", new JsonValue.String("question")),
            M("author", new JsonValue.String("https://agents.example/bob")),
            M("board", new JsonValue.String(board)),
            M("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            M("created_at", new JsonValue.String("2026-09-04T12:00:00Z")),
            M("nonce", new JsonValue.String("n")),
            M("body", new JsonValue.String("some body")),
            M("title", new JsonValue.String("A title")),
        };
        var read = PostEnvelope.Read(new JsonValue.Object([.. members]));
        Assert.True(read.TryGetValue(out var envelope, out var error), error?.Type);
        return envelope!;
    }

    /// <summary>The rows an envelope can carry: not empty, and a string <see cref="BoardName"/> accepts.</summary>
    private static (string Name, string Board, string[] Expected)[] Reachable() =>
        [.. BoardSpellings.Table().Where(r => r.Board.Length > 0 && BoardName.Parse(r.Board).TryGetValue(out _, out _))];

    public static TheoryData<int> Rows()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Reachable().Length; i++) data.Add(i);
        return data;
    }

    private EnrolledAgent Agent()
    {
        Assert.True(
            new ProfileStore(_home).Create("alice", "https://agents.example/alice", "alice-1", new Uri("http://forum.test")).TryGetValue(out var agent, out var error),
            error?.Detail);
        return agent!;
    }

    /// <summary>NAM8: for each reachable spelling the write side and the signing side give the table's verdict, so neither admits what the other refuses.</summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void R8_85_NAM8_TheWritePredicateAndTheSigningPredicateAreOne(int row)
    {
        var (name, board, expected) = Reachable()[row];
        var refuse = expected.Length > 0;

        var writeRefuses = PostEnvelope.AdmissionRefusal(QuestionOn(board)) is not null;
        using var agent = Agent();
        var built = SubmissionBuilder.Build(agent, new PostDraft { Kind = PostKind.Question, Board = board, Title = "A title", Body = "some body" }, DateTimeOffset.UnixEpoch);
        var signingRefuses = !built.TryGetValue(out _, out var error);

        Assert.True(refuse == writeRefuses, $"{name}: the route's function {(writeRefuses ? "refused" : "admitted")} a board the table {(refuse ? "refuses" : "admits")}");
        Assert.True(refuse == signingRefuses, $"{name}: Build {(signingRefuses ? "refused" : "returned a signed submission for")} a board the table {(refuse ? "refuses" : "admits")}");
        if (refuse)
        {
            Assert.Equal("curia/client/envelope-invalid", error!.Type);
            Assert.Contains(PublishedBoardWords.BoardType(), error.Detail ?? string.Empty, StringComparison.Ordinal);
            foreach (var cls in expected)
                Assert.Contains(cls, error.Detail ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>NAM8: the table reaches an envelope with the rows it should; the two skipped rows are named.</summary>
    [Fact]
    public void R8_85_NAM8_TheReachableRowsAreTheTableMinusTheEmptyBoardAndTheLoneSurrogates()
    {
        var all = BoardSpellings.Table();
        var skipped = all.Where(r => r.Board.Length == 0 || !BoardName.Parse(r.Board).TryGetValue(out _, out _)).Select(r => r.Name).ToArray();

        Assert.Equal(3, skipped.Length);
        Assert.Contains(skipped, n => n.Contains("empty", StringComparison.Ordinal));
        Assert.Equal(2, skipped.Count(n => n.Contains("lone", StringComparison.Ordinal)));
    }
}
