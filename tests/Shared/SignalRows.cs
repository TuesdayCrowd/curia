using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Tests.Shared;

/// <summary>
/// A <c>post.accepted</c> event for a vote or a verification, built the way the ingest pipeline builds one (its signed canonical text carried as a string beside the Forum's own members), with
/// the target and one other string member chosen by the caller. Compiled into the Infrastructure and Api tests of R11.6 (add.): the other member is where a probe puts a NUL escape or a
/// very long string, which ADMIT admits and a database must be able to store.
/// </summary>
internal static class SignalRows
{
    /// <summary>The digest of the post number <paramref name="n"/>: the prefixed form, 64 lowercase hex digits.</summary>
    public static string Digest(int n) => "sha256:" + n.ToString("x64", CultureInfo.InvariantCulture);

    /// <summary>The event of a vote (<paramref name="kind"/> <c>vote</c>) or verification, targeting <paramref name="target"/>; <paramref name="nonce"/> is the envelope's nonce member, any string.</summary>
    public static DomainEvent Make(string kind, int number, string target, string nonce, string author = "https://agents.example/signals/a0")
    {
        var id = "01SG" + number.ToString("D22", CultureInfo.InvariantCulture);
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(kind)));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String("b")));
        if (kind != "vote") members.Add(new("body", new JsonValue.String("a body")));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", kind == "verification"
            ? new JsonValue.Array([new JsonValue.Object([new("kind", new JsonValue.String("url")), new("target", new JsonValue.String("https://example.test/" + number.ToString(CultureInfo.InvariantCulture)))])])
            : new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String("2026-11-27T12:00:00Z")));
        members.Add(new("nonce", new JsonValue.String(nonce)));
        members.Add(new("target", new JsonValue.String(target)));
        if (kind == "vote")
        {
            members.Add(new("endorse", new JsonValue.Bool(true)));
            members.Add(new("predicted_endorsement_bp", new JsonValue.Number(5000)));
            members.Add(new("epoch", new JsonValue.Number(1)));
        }

        if (kind == "verification")
        {
            members.Add(new("method", new JsonValue.String("ran it")));
            members.Add(new("result", new JsonValue.String("reproduced")));
        }

        var canonical = CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable())).Match(v => v, e => throw new InvalidOperationException(e.Type));
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(id)),
            new("canonical", new JsonValue.String(System.Text.Encoding.UTF8.GetString(canonical.Span))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String(Digest(number))),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String("b")),
            new("kind", new JsonValue.String(kind)),
            new("parent", new JsonValue.Null()),
        ]);

        return new DomainEvent(
            EventId.Create(id).Match(v => v, e => throw new InvalidOperationException(e.Type)),
            EventType.Create("post.accepted").Match(v => v, e => throw new InvalidOperationException(e.Type)),
            ActorId.Create(author).Match(v => v, e => throw new InvalidOperationException(e.Type)),
            payload);
    }
}
