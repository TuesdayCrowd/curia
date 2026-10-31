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
/// PR 3 (errata G50, R8.80), Task 0's probes RLW16 to RLW18 against a real Forum on real Postgres: the acceptance receipt, <c>201</c> and <c>200</c> alike, always
/// carries <c>post_kind</c> and <c>text_of</c>, told only to the asker, for an answer, a revision, a revision of a revision, a revision whose <c>parent</c> lies and a
/// revision and the root itself of an answer the Forum does not serve.
///
/// <para><b>Oracles.</b> <c>post_kind</c> is the post's kind by the <c>events</c> table and <c>text_of</c> is the root of the correction chain walked by this file's own
/// helper over the payload <c>prev</c> digests (never over <c>parent</c>, and never by <c>CorrectionIndex</c>), by SQL from the connection string the fixture holds; the
/// member names, kind words and relation values are G50's and G44's, parsed from the errata. The response is read raw: a deserializer would collapse an absent member
/// and a null one. The lying-parent and withheld-root cases are the shapes Task 0's M2 measured (the route accepts both and <c>chainRoot</c> is walked over the
/// unfiltered log).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptReceiptKindEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Reply(HttpStatusCode Status, string Raw, JsonElement Body);

    private async Task<Reply> AcceptAsync(SceneActor actor, string postId)
    {
        using var response = await actor.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{postId}/accept",
            await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, Ct, contentType: "application/json");
        var raw = await response.Content.ReadAsStringAsync(Ct);
        return new Reply(response.StatusCode, raw, raw.StartsWith('{') ? JsonDocument.Parse(raw).RootElement.Clone() : default);
    }

    private Task<Posted> ReviseAsync(Scene scene, Posted revised, string body, string? parent = null) =>
        Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, body, parent ?? revised.PostId, revised.Digest, forum.Now), Ct);

    /// <summary>The post's kind and its chain root, from the events table by this helper's own walk over <c>prev</c> (never over <c>parent</c>).</summary>
    private async Task<(string Kind, string TextOf)> ExpectedAsync(string postId)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        var rows = new Dictionary<string, (string Id, string Kind, string? Prev)>(StringComparer.Ordinal);
        await using (var query = new NpgsqlCommand("SELECT payload->>'digest', aggregate_id, payload->>'kind', (payload->>'canonical')::jsonb->>'prev' FROM events WHERE event_type = 'post.accepted';", connection))
        await using (var reader = await query.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct)) rows[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), await reader.IsDBNullAsync(3, Ct) ? null : reader.GetString(3));
        }

        var start = rows.Values.Single(r => r.Id == postId);
        var current = start;
        while (current.Kind == "revision" && current.Prev is { } prev && rows.TryGetValue(prev, out var up)) current = up;
        return (start.Kind, start.Kind == "revision" ? current.Id : start.Id);
    }

    /// <summary>
    /// RLW16. The receipt's members, by the log. For an answer, a revision (depth 1), a revision of a revision (depth 2), a revision whose <c>parent</c> lies
    /// (hand-signed, naming another post), and a revision and the root itself of an answer the Forum does not serve: the <c>201</c> and, after a replay, the <c>200</c>
    /// carry <c>post_kind</c> and <c>text_of</c>; <c>post_kind</c> is the post's kind by the events table; <c>text_of</c> is the root of the chain by <c>prev</c>.
    /// </summary>
    [Fact]
    public async Task R8_80_RLW16_TheReceiptCarriesWhatThePostIsAndWhoseTextItIs()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (kindMember, textOfMember) = PublishedReadersLastWords.ReceiptMembers();

        var depth1 = await ReviseAsync(scene, scene.Answer2, $"A corrected second answer about {scene.Term}.");
        var depth2 = await ReviseAsync(scene, depth1, $"A corrected corrected second answer about {scene.Term}.");
        var lying = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.", parent: scene.Comment.PostId);
        var third = await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignAnswer(scene.Board, $"A third way to settle {scene.Term}.", scene.Question.PostId, forum.Now), Ct);
        var ofWithheld = await ReviseAsync(scene, third, $"A corrected third answer about {scene.Term}.");
        await forum.WithholdAsync(third.PostId, Ct);

        var problems = new List<string>();
        foreach (var (name, post) in new[]
        {
            ("an answer", scene.Answer1), ("a revision", depth1), ("a revision of a revision", depth2), ("a revision whose parent lies", lying),
            ("a revision of a withheld answer", ofWithheld), ("the withheld answer itself", third),
        })
        {
            var (kind, textOf) = await ExpectedAsync(post.PostId);
            foreach (var (label, expectedStatus) in new[] { ("first", HttpStatusCode.Created), ("replay", HttpStatusCode.OK) })
            {
                var reply = await AcceptAsync(scene.Asker, post.PostId);
                if (reply.Status != expectedStatus) { problems.Add($"{name} ({label}): Expected: {(int)expectedStatus} Actual: {(int)reply.Status} {reply.Raw}"); continue; }
                if (!reply.Body.TryGetProperty(kindMember, out var gotKind)) problems.Add($"{name} ({label}): Expected: member {kindMember} Actual: absent from {reply.Raw}");
                else if (gotKind.GetString() != kind) problems.Add($"{name} ({label}): {kindMember} Expected: {kind} Actual: {gotKind}");
                if (!reply.Body.TryGetProperty(textOfMember, out var gotText)) problems.Add($"{name} ({label}): Expected: member {textOfMember} Actual: absent from {reply.Raw}");
                else if (gotText.GetString() != textOf) problems.Add($"{name} ({label}): {textOfMember} Expected: {textOf} Actual: {gotText}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// RLW17. A replacing acceptance: the members describe the post named and not the post replaced, and <c>replaced_answer</c> and <c>replaced_relation</c> are as R8.76
    /// says (the existing facts of <c>AcceptRevisionEndpointTests</c> hold those on their own).
    /// </summary>
    [Fact]
    public async Task R8_80_RLW17_AReplacingAcceptanceDescribesThePostNamedAndNotThePostReplaced()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (kindMember, textOfMember) = PublishedReadersLastWords.ReceiptMembers();
        var (answerWord, revisionWord) = PublishedReadersLastWords.KindWords();
        var relation = PublishedReadersWords.R876Member();
        var values = PublishedReadersWords.R876Values();
        var revision = await ReviseAsync(scene, scene.Answer2, $"A corrected second answer about {scene.Term}.");

        _ = await AcceptAsync(scene.Asker, scene.Answer1.PostId);
        var toOther = await AcceptAsync(scene.Asker, revision.PostId);
        var withinChain = await AcceptAsync(scene.Asker, scene.Answer2.PostId);

        var problems = new List<string>();
        void Expect(string name, Reply reply, string replaced, string relationValue, string kind, string textOf)
        {
            if (reply.Status != HttpStatusCode.Created) { problems.Add($"{name}: Expected: 201 Actual: {(int)reply.Status} {reply.Raw}"); return; }
            if (reply.Body.GetProperty("replaced_answer").GetString() != replaced) problems.Add($"{name}: replaced_answer Expected: {replaced} Actual: {reply.Body.GetProperty("replaced_answer")}");
            if (reply.Body.GetProperty(relation).GetString() != relationValue) problems.Add($"{name}: {relation} Expected: {relationValue} Actual: {reply.Body.GetProperty(relation)}");
            if (!reply.Body.TryGetProperty(kindMember, out var gotKind) || gotKind.GetString() != kind) problems.Add($"{name}: {kindMember} Expected: {kind} (the post named) Actual: {(reply.Body.TryGetProperty(kindMember, out var k) ? k.ToString() : "absent")}");
            if (!reply.Body.TryGetProperty(textOfMember, out var gotText) || gotText.GetString() != textOf) problems.Add($"{name}: {textOfMember} Expected: {textOf} (the post named's answer) Actual: {(reply.Body.TryGetProperty(textOfMember, out var t) ? t.ToString() : "absent")}");
        }

        Expect("to another answer's revision", toOther, scene.Answer1.PostId, values[1], revisionWord, scene.Answer2.PostId);
        Expect("within the chain, back to the answer", withinChain, revision.PostId, values[0], answerWord, scene.Answer2.PostId);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// RLW18. Told only to the asker: a non-asker's acceptance is <c>403</c> <c>table-10/own-thread-only</c> and its body holds neither <c>post_kind</c> nor
    /// <c>text_of</c> nor the chain root's id; the asker's <c>201</c> in the same fact carries both.
    /// </summary>
    [Fact]
    public async Task R8_80_RLW18_AStrangerIsToldNothingOfTheChain()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (kindMember, textOfMember) = PublishedReadersLastWords.ReceiptMembers();
        var revision = await ReviseAsync(scene, scene.Answer2, $"A corrected second answer about {scene.Term}.");

        var stranger = await AcceptAsync(scene.Answerer, revision.PostId);
        var asker = await AcceptAsync(scene.Asker, revision.PostId);

        var problems = new List<string>();
        if (stranger.Status != HttpStatusCode.Forbidden || !stranger.Raw.Contains("table-10/own-thread-only", StringComparison.Ordinal)) problems.Add($"a non-asker: Expected: 403 table-10/own-thread-only Actual: {(int)stranger.Status} {stranger.Raw}");
        foreach (var leaked in new[] { kindMember, textOfMember, scene.Answer2.PostId })
            if (stranger.Raw.Contains(leaked, StringComparison.Ordinal)) problems.Add($"a non-asker's refusal holds \"{leaked}\"");
        if (asker.Status != HttpStatusCode.Created) problems.Add($"the asker: Expected: 201 Actual: {(int)asker.Status} {asker.Raw}");
        else
        {
            if (!asker.Body.TryGetProperty(kindMember, out _)) problems.Add($"the asker: Expected: member {kindMember} Actual: absent from {asker.Raw}");
            if (!asker.Body.TryGetProperty(textOfMember, out var textOf) || textOf.GetString() != scene.Answer2.PostId) problems.Add($"the asker: Expected: {textOfMember} = {scene.Answer2.PostId} Actual: {asker.Raw}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
