using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Projections;

/// <summary>
/// Builds the searchable read model out of the event stream: the fields a lexical query matches
/// against, and nothing else.
///
/// <para><b>Why this is not three more fields on <see cref="PostView"/>.</b> Title, body and tags
/// live inside the canonical envelope, and the serving path deliberately treats <c>canonical</c> as
/// an opaque string it hands to the datamarker — it has never needed the envelope's structure.
/// Widening <see cref="PostView"/> would put an envelope parse on every read path in order to serve
/// one, so the parse and its blast radius stay here. This is the mapping
/// <see cref="LexicalSearch"/>'s own remarks anticipate when they say the domain "should not know
/// what a read model looks like" and that Application maps its view onto
/// <see cref="SearchablePost"/>.</para>
///
/// <para><b>Withheld posts are excluded here, not at the route.</b> R10.36's remedy is withholding,
/// and a post that stays out of <c>GET /v1/posts/{id}</c> while surfacing in search is the
/// withholding not having happened — search being, if anything, the likelier way someone finds it.
/// One filter in the projection is one place to get right; a filter per caller is one chance per
/// caller to forget.</para>
///
/// <para><b>No clock</b>, for the reason every other projector here records: a rebuild that
/// consulted "now" would make R11.9's replay drill tautological.</para>
/// </summary>
public static class SearchProjector
{
    /// <summary>
    /// Folds a seq-ordered event list into the servable searchable corpus, in ascending <c>seq</c>
    /// order — which is the order <see cref="LexicalSearch.Search"/> documents that it wants.
    /// </summary>
    public static ImmutableArray<SearchablePost> Fold(IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

        var servable = FlagProjector.Fold(eventsInSeqOrder);

        // R10.7's owner arm. The cap lives in HybridRanking.Diversify, but it can only group what
        // this fold supplies: with the owner unset every post is its own owner and the owner cap
        // degrades silently to the author cap it exists to reinforce -- which an adversary defeats
        // by giving each post its own agent, since attest-owner binds one agent at a time and caps
        // the agents an owner may hold at nothing. Folded here, once, for the same reason `servable`
        // is: it is a property of the whole log rather than of one event, so TryRead cannot see it.
        var standings = AgentStandingProjector.Fold(eventsInSeqOrder);

        var posts = ImmutableArray.CreateBuilder<SearchablePost>();
        var lastSeq = EventSequence.Zero;

        foreach (var appended in eventsInSeqOrder)
        {
            if (appended.Seq < lastSeq)
                throw new ArgumentException(
                    "Events must arrive in ascending seq order; every IEventReader this solution " +
                    "ships already guarantees that, so a violation means the caller did not get " +
                    "these from a store's forward scan.",
                    nameof(eventsInSeqOrder));

            lastSeq = appended.Seq;

            var post = TryRead(appended);
            if (post is null) continue;

            if (servable.TryGetValue(post.PostId, out var moderation) && !moderation.MayServe) continue;

            // OwnerVerified is deliberately not consulted. Only an operator can append an
            // attestation (R4.30), so an owner id in the log is an operator's record that these
            // agents share an owner whether or not the proof met R4.24's bar -- and a shared owner
            // is exactly what diversification groups on. Reading the flag here would let an
            // adversary avoid the cap by taking the weaker attestation.
            posts.Add(post with
            {
                Owner = standings.TryGetValue(post.Author, out var standing) ? standing.OwnerId : null,
            });
        }

        return posts.ToImmutable();
    }

    /// <summary>
    /// The searchable view of one event, or <see langword="null"/> when the event is not a
    /// discussion post: what <see cref="Fold"/> reads per event, exposed so the vector index embeds
    /// exactly the text search sees and nothing else.
    /// </summary>
    public static SearchablePost? TryRead(AppendedEvent appended)
    {
        ArgumentNullException.ThrowIfNull(appended);

        if (appended.Event.Type.Value != PostProjector.PostAcceptedType) return null;
        if (appended.Event.Payload is not JsonValue.Object payload) return null;

        return ReadSearchable(payload, appended);
    }

    private static SearchablePost? ReadSearchable(JsonValue.Object payload, AppendedEvent appended)
    {
        var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var member in payload.Members)
            fields[member.Key] = member.Value;

        if (!Str(fields, "post_id", out var postId)) return null;
        if (!Str(fields, "digest", out var digest)) return null;
        if (!Str(fields, "canonical", out var canonical)) return null;

        // The envelope is re-derived from the persisted bytes rather than carried alongside them.
        // R6.12's byte-identity makes that safe: `canonical` is exactly what VERIFY consumed, so a
        // view derived from it is a view of what the author signed. Storing a parsed copy beside it
        // would be a second answer to a question the bytes already settle.
        var parsed = JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(canonical));
        if (!parsed.TryGetValue(out var value, out _)) return null;
        if (value is not JsonValue.Object root) return null;
        if (!PostEnvelope.Read(root).TryGetValue(out var envelope, out _)) return null;

        // A vote is never served before its epoch is sealed (R8.55) and a verification report is
        // read on its result's envelope, not found by keyword; neither is discussion (R8.59).
        if (!PostKinds.IsDiscussion(envelope!.Kind)) return null;

        // A payload nothing can parse is skipped rather than thrown. Nothing should be able to put
        // one in the log -- PERSIST writes only what VERIFY consumed -- but a projection that threw
        // would take down every read path over one bad row, and R11.9's replay would stop at it
        // permanently rather than degrade by one post.
        return new SearchablePost(
            postId,
            digest,
            envelope.Board,
            envelope.Kind,
            envelope.Title,
            envelope.Body,
            envelope.Tags,
            envelope.Author,
            appended.Seq.Value,
            PostProjector.PossibleDuplicateOf(fields));
    }

    private static bool Str(Dictionary<string, JsonValue> fields, string name, out string value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.String s)
        {
            value = s.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
