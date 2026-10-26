using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Posted = Curia.Api.Tests.ScenePost;
using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// R8.71 (errata G40): the accept route's receipt carries <c>replaced_answer</c>, always: the post id of the answer whose standing acceptance the call
/// replaced, or <c>null</c> where none stood and where the answer named already stood. The oracle is the <c>answer.accepted</c> events the
/// <c>events</c> table holds, in order, read by SQL; the member's name is parsed from the errata; the ids are the receipts of the posts the test made.
/// The raw response text is what is read, since a deserializer collapses an absent member and a null one.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptReplacedEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private static readonly string Member = PublishedReadersWords.R871Member();

    private sealed record Receipt(HttpStatusCode Status, string Raw, JsonElement Body);

    private async Task<Receipt> AcceptRawAsync(Scene scene, Posted answer, CancellationToken ct)
    {
        using var response = await scene.Asker.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{answer.PostId}/accept",
            await scene.Asker.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, ct, contentType: "application/json");
        var raw = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, raw);
        return new Receipt(response.StatusCode, raw, JsonDocument.Parse(raw).RootElement.Clone());
    }

    /// <summary>The answers the log recorded as accepted for the thread, in order: the oracle for every id below.</summary>
    private async Task<string[]> AcceptedInOrderAsync(string root, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var query = new NpgsqlCommand(
            "SELECT payload->>'answer_id' FROM events WHERE event_type = 'answer.accepted' AND payload->>'thread_root' = @root ORDER BY seq;", connection);
        query.Parameters.AddWithValue("root", root);
        var ids = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return [.. ids];
    }

    private static string ReplacedOf(Receipt receipt)
    {
        Assert.True(
            receipt.Body.TryGetProperty(Member, out var member),
            $"Expected: member \"{Member}\" Actual: missing from the {(int)receipt.Status} response {receipt.Raw}");
        return member.ValueKind == JsonValueKind.Null ? "(null)" : member.GetString()!;
    }

    [Fact]
    public async Task R8_71_TheReceiptCarriesReplacedAnswerAlways()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);

        var first = await AcceptRawAsync(scene, scene.Answer1, ct);
        var switched = await AcceptRawAsync(scene, scene.Answer2, ct);
        var replay = await AcceptRawAsync(scene, scene.Answer2, ct);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.Created, switched.Status);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.False(first.Body.GetProperty("already_accepted").GetBoolean());
        Assert.False(switched.Body.GetProperty("already_accepted").GetBoolean());
        Assert.True(replay.Body.GetProperty("already_accepted").GetBoolean());

        // Present on all three, and the null is JSON null (a first acceptance and a replay replaced nothing).
        Assert.Equal("(null)", ReplacedOf(first));
        Assert.Equal(scene.Answer1.PostId, ReplacedOf(switched));
        Assert.Equal("(null)", ReplacedOf(replay));
    }

    [Fact]
    public async Task R8_71_ASwitchBackNamesTheAnswerItReplaced()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);

        var receipts = new List<Receipt>
        {
            await AcceptRawAsync(scene, scene.Answer1, ct),
            await AcceptRawAsync(scene, scene.Answer2, ct),
            await AcceptRawAsync(scene, scene.Answer1, ct),
        };

        // The log's own account of what stood before each call.
        var events = await AcceptedInOrderAsync(scene.Question.PostId, ct);
        Assert.Equal([scene.Answer1.PostId, scene.Answer2.PostId, scene.Answer1.PostId], events);

        Assert.Equal("(null)", ReplacedOf(receipts[0]));
        for (var i = 1; i < receipts.Count; i++)
            Assert.Equal(events[i - 1], ReplacedOf(receipts[i]));
    }

    [Fact]
    public async Task R8_71_ADisplacedAnswerTheForumWithholdsIsStillNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await AcceptRawAsync(scene, scene.Answer1, ct);
        await forum.WithholdAsync(scene.Answer1.PostId, ct);

        var switched = await AcceptRawAsync(scene, scene.Answer2, ct);

        Assert.Equal(scene.Answer1.PostId, ReplacedOf(switched));
    }

    /// <summary>The control: the log's event carries no replaced answer (history is derivable by replay), so the member is the receipt's and the event is unchanged.</summary>
    [Fact]
    public async Task R8_71_TheLogsEventCarriesNoReplacedAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await AcceptRawAsync(scene, scene.Answer1, ct);
        await AcceptRawAsync(scene, scene.Answer2, ct);

        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var query = new NpgsqlCommand(
            "SELECT payload::text FROM events WHERE event_type = 'answer.accepted' AND payload->>'thread_root' = @root ORDER BY seq DESC LIMIT 1;", connection);
        query.Parameters.AddWithValue("root", scene.Question.PostId);
        var payload = JsonDocument.Parse((string)(await query.ExecuteScalarAsync(ct))!).RootElement;

        Assert.Equal(["accepted_by", "answer_id", "thread_root"], payload.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(scene.Answer2.PostId, payload.GetProperty("answer_id").GetString());
    }
}
