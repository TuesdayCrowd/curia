using System.Buffers.Text;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;
using Curia.OperatorTool;

namespace Curia.Api.Tests;

/// <summary>
/// A signed <c>moderation.applied</c> row for the late-commit probes (Task 3d, Amendment 7 ruling 89): the row a holder commits behind a request, which the Forum must honour as it honours the one
/// <c>curia-operator moderate</c> appends. Built from public types alone (<see cref="CanonicalJson"/>, <see cref="OperatorActsSigningKey"/> and the BCL's base64url), so that no production assembly gains an
/// unsigned writer, a public signing seam or a wider <c>InternalsVisibleTo</c>.
///
/// <para><b>The member list is written from the published text, not from the production builder.</b> R6.67 step 3's closed form of a moderation record is <c>actor_id</c>, <c>adjudicates</c>, <c>category</c>,
/// <c>digest</c>, <c>effect</c>, <c>moderator</c>, <c>post_id</c>, <c>prev</c>, <c>rationale</c>, <c>typ</c> (<c>typ</c> is <c>curia.moderation/1</c>, <c>moderator</c> is <c>human</c>), and the row states
/// only <c>statement</c> and <c>authorization</c> (conformance vector 74's shape; every member an honoured act acts on is read from the statement). A drift between this file and
/// <c>OperatorActStatements</c> shows as the fold skipping the row (<c>act-statement-mismatch</c>, <c>act-broken-link</c> or <c>act-signature-invalid</c>), which the probe that uses it reads as a red.</para>
///
/// <para><b><c>prev</c> and the log index.</b> R6.67 step 4 compares <c>prev</c> with a log index, a position in the whole log counted from 0 (R6.47): the number of log entries before the row, in
/// <c>seq</c> order, whatever <c>seq</c> values the store handed out. <c>seq</c> is a sequence, not a position (a rolled-back transaction leaves a gap, R11.6), so <see cref="LogIndexSql"/> counts entries of
/// smaller <c>seq</c> instead of subtracting one.</para>
/// </summary>
internal static class LateSignedModeration
{
    /// <summary>The moderation statement's <c>typ</c> (R6.67 step 2).</summary>
    private const string StatementTyp = "curia.moderation/1";

    /// <summary>The detached authorization's header <c>typ</c> (R6.67 step 5).</summary>
    private const string AuthorizationTyp = "curia-operator-act+jws";

    /// <summary>The actor every late row is written under; the statement states the same actor (R6.67 step 3).</summary>
    internal const string Actor = "operator:fixture";

    /// <summary>The SQL that turns a <c>seq</c> into a log index on the owner's connection: how many entries come before it.</summary>
    internal const string LogIndexSql = "SELECT count(*) FROM events WHERE seq < @seq;";

    /// <summary>A built row: the event id and actor to insert it under, and the payload JSON (<c>statement</c> and <c>authorization</c> only).</summary>
    internal sealed record Row(string EventId, string Author, string PayloadJson);

    /// <summary>
    /// The row that moderates <paramref name="postId"/> (whose accepted envelope has digest <paramref name="digest"/>) with <paramref name="effect"/>, chained after the log index <paramref name="prev"/> and
    /// signed under the key of <paramref name="operatorActsKeyPem"/> (the fixture's, already published).
    /// </summary>
    internal static Row Build(string operatorActsKeyPem, string eventId, string postId, string digest, string effect, long prev)
    {
        ArgumentNullException.ThrowIfNull(operatorActsKeyPem);
        ArgumentNullException.ThrowIfNull(postId);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(effect);

        var statement = new JsonValue.Object(
        [
            new("actor_id", new JsonValue.String(Actor)),
            new("adjudicates", new JsonValue.Array(ImmutableArray<JsonValue>.Empty)),
            new("category", new JsonValue.String("spam")),
            new("digest", new JsonValue.String(digest)),
            new("effect", new JsonValue.String(effect)),
            new("moderator", new JsonValue.String("human")),
            new("post_id", new JsonValue.String(postId)),
            new("prev", new JsonValue.Number(prev)),
            new("rationale", new JsonValue.String("moderated by the late-commit probe")),
            new("typ", new JsonValue.String(StatementTyp)),
        ]);

        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(statement)).ToArray();
        using var key = OperatorActsSigningKey.FromPem(operatorActsKeyPem);

        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"{{\"alg\":\"ES256\",\"b64\":false,\"crit\":[\"b64\"],\"kid\":\"{key.Kid}\",\"typ\":\"{AuthorizationTyp}\"}}"));
        var signingInput = Encoding.ASCII.GetBytes(header + ".").Concat(canonical).ToArray();
        var signature = Require(key.SignStatement(signingInput));

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["statement"] = Encoding.UTF8.GetString(canonical),
            ["authorization"] = $"{header}..{Base64Url.EncodeToString(signature)}",
        });
        return new Row(eventId, Actor, payload);
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));
}
