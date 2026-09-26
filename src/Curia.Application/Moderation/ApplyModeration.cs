using System.Collections.Immutable;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Moderation;

/// <summary>What the log recorded when a moderation record was accepted.</summary>
/// <param name="Adjudicates">The flags the record names (R10.60): every flag of its category on the post, derived, never typed.</param>
/// <param name="At">The store's <c>server_ts</c> for the append.</param>
public sealed record ModerationRecorded(
    string PostId,
    string Digest,
    ModerationEffect Effect,
    FlagKind Category,
    ImmutableArray<string> Adjudicates,
    ActorId Moderator,
    DateTimeOffset At);

/// <summary>
/// R10.59's human arm: an operator's moderation record, appended out of band.
///
/// <para><b>No HTTP route reaches this.</b> Table 10 grants <c>moderation</c>|<c>apply</c> only to a
/// delegated T3 agent (Phase 4); an operator endpoint would need a pair that does not exist, and
/// <c>ResourceActionModel.RowFor</c> reports an unmodelled pair as a failure. The operator tool calls
/// this over the event store's append-only grant, as it calls <c>AttestOwner</c> (errata G5).</para>
///
/// <para><b>What a record carries (R10.60).</b> The post and its envelope digest (R6.25), the
/// moderator kind and actor, the effect and category, a screened rationale, and the flags it
/// adjudicates — derived here from the flag directory, never typed by the moderator, because the
/// record is the only place a reader of the public log learns which flags were reviewed.</para>
///
/// <para><b>A record that changes nothing is refused.</b> R10.39 counts records. A record that
/// changes neither which categories hold the post, or how (<see cref="ModerationPolicy.ServingEffect"/>),
/// nor any flag's upheld state, and names no flag no earlier record named, would inflate the counts
/// while recording no decision.</para>
///
/// <para><b>A record acts only on the category it cites (R10.61).</b> So a restore in a category
/// that holds nothing is refused, as is a dismissal in one that holds the post.</para>
/// </summary>
public sealed class ApplyModeration
{
    /// <summary>The actor namespace R10.59 gives the human arm (plan D4: a convention the domain cannot enforce, so it is enforced here).</summary>
    public const string OperatorPrefix = "operator:";

    private readonly IEventStore _events;
    private readonly IFlagDetailStore _details;
    private readonly TimeProvider _clock;
    private readonly UlidGenerator _ids;

    public ApplyModeration(IEventStore events, IFlagDetailStore details, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _details = details;
        _clock = clock;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>Records <paramref name="effect"/> on <paramref name="postId"/> in <paramref name="category"/>, or reports why not.</summary>
    public async Task<Result<ModerationRecorded>> RecordAsync(
        string postId,
        ModerationEffect effect,
        FlagKind category,
        string rationale,
        ActorId moderator,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        if (moderator.Value is null
            || !moderator.Value.StartsWith(OperatorPrefix, StringComparison.Ordinal)
            || moderator.Value.Length == OperatorPrefix.Length)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NotAnOperator());

        // "operator: " passes the prefix test and names no one, in a leaf that is public and permanent.
        if (moderator.Value.AsSpan(OperatorPrefix.Length).IsWhiteSpace())
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.BlankOperatorName());

        if (string.IsNullOrWhiteSpace(rationale))
            return Result<ModerationRecorded>.Fail(ModerationErrors.RationaleRequired());

        // R10.60: the rationale lands in a leaf R6.51 serves verbatim, under the same two-regime
        // table a flag's rationale is screened with.
        var screened = ContentScreener.ScreenText(Encoding.UTF8.GetBytes(rationale));
        if (!screened.TryGetValue(out var screening, out var screeningError))
            return Result<ModerationRecorded>.Fail(screeningError!);

        if (!screening!.MayPersist)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.RationaleRejected(screening.Annotations));

        if (!AggregateId.Create(postId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<ModerationRecorded>.Fail(aggregateError!);

        var read = await _events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var log, out var readError))
            return Result<ModerationRecorded>.Fail(readError!);

        var post = PostProjector.Fold(log!).FirstOrDefault(p => string.Equals(p.PostId, postId, StringComparison.Ordinal));
        if (post is null)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NoSuchPost(postId));

        var rows = await _details.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!rows.TryGetValue(out var details, out var detailError))
            return Result<ModerationRecorded>.Fail(detailError!);

        ImmutableArray<RaisedFlag> onPost =
        [
            .. FlagDirectory.Join(log!, details!).Flags
                .Where(f => string.Equals(f.PostId, postId, StringComparison.Ordinal)),
        ];

        // R10.60: every flag of this category raised against the post so far — derived, never typed.
        ImmutableArray<string> adjudicates =
        [
            .. onPost
                .Where(f => f.Kind == category)
                .Select(f => f.FlagId),
        ];

        // R10.62: a raiser and a rationale are published never, and this reason is published always
        // (R10.60), so a reason repeating either, for any flag on the post, is refused — never repaired
        // (R10.26). The refusal names the field alone: the matched text, or the flag, would disclose it.
        if (FlagDisclosure.Repeated(rationale, onPost, FlagDirectory.RationalesByFlag(log!, details!)) is { } field)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.RationaleDisclosesFlag(field));

        ImmutableArray<ModerationAction> before = FlagProjector.Fold(log!).TryGetValue(postId, out var moderation)
            ? moderation.History
            : [];

        var action = new ModerationAction(
            postId, ModeratorKind.Human, moderator.Value, effect, category, rationale, ServerTimestamp.At(_clock.GetUtcNow()), adjudicates);

        if (!ModerationPolicy.Authorize(action).TryGetValue(out _, out var authorizeError))
            return Result<ModerationRecorded>.Fail(authorizeError!);

        ImmutableArray<ModerationAction> after = [.. before, action];
        var holdsBefore = ModerationPolicy.ServingEffect(before);
        var noOp = SameHolds(holdsBefore, ModerationPolicy.ServingEffect(after))
            && ModerationPolicy.UpheldFlags(before).SetEquals(ModerationPolicy.UpheldFlags(after))
            && ModerationPolicy.AdjudicatedFlags(before).IsSupersetOf(adjudicates);
        if (noOp)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NoOp(postId, effect, category));

        // R10.61: a restore releases only the hold of the category it cites. One citing a category that
        // holds nothing would release nothing, and would still release the flags it names while another
        // category's hold kept the post unserved.
        if (effect == ModerationEffect.Restore && !holdsBefore.ContainsKey(category))
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.RestoreOfUnheldCategory(postId, category));

        // R10.61: a dismissal holds and releases nothing. In a category that holds the post, it would
        // leave the post withheld with the flags behind the hold no longer upheld.
        if (effect == ModerationEffect.Dismiss && holdsBefore.ContainsKey(category))
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.DismissalOfHeldCategory(postId, category));

        // The expected version comes from the read the decision was made on, not from a second read.
        // A record another operator appended to this post since then fails this append, rather than
        // standing beside a record decided without it (R10.39 counts records). A flag committed since
        // is on its own aggregate and is not caught here; it stays open until a later record names it.
        if (!AggregateVersion.From(log!.Count(e => e.AggregateId == aggregate)).TryGetValue(out var version, out var versionError))
            return Result<ModerationRecorded>.Fail(versionError!);

        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<ModerationRecorded>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<ModerationRecorded>.Fail(eventIdError!);

        if (!EventType.Create(FlagProjector.ModerationAppliedType).TryGetValue(out var type, out var typeError))
            return Result<ModerationRecorded>.Fail(typeError!);

        var payload = new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.DigestField, new JsonValue.String(post.Digest)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(action.Moderator))),
            new(FlagProjector.ActorIdField, new JsonValue.String(moderator.Value)),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(effect))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String(rationale)),
            new(FlagProjector.AdjudicatesField, new JsonValue.Array([.. adjudicates.Select(id => (JsonValue)new JsonValue.String(id))])),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, version, [new DomainEvent(eventId, type, moderator, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(events => new ModerationRecorded(
            postId, post.Digest, effect, category, adjudicates, moderator, events[0].ServerTimestamp.Value));
    }

    /// <summary>
    /// Whether two <see cref="ModerationPolicy.ServingEffect"/> maps hold the post in the same
    /// categories, the same way: compared element by element, since an immutable map's own
    /// <c>Equals</c> compares references.
    /// </summary>
    private static bool SameHolds(
        ImmutableSortedDictionary<FlagKind, ModerationEffect> first,
        ImmutableSortedDictionary<FlagKind, ModerationEffect> second) =>
        first.Count == second.Count
        && first.All(hold => second.TryGetValue(hold.Key, out var effect) && effect == hold.Value);
}

/// <summary>RFC 9457 problem-type slugs the moderation writer emits.</summary>
public static class ModerationRecordErrors
{
    /// <summary>R10.59: the human arm is an operator's, named <c>operator:&lt;name&gt;</c>.</summary>
    public static Error NotAnOperator() => new(
        "curia/moderation/not-an-operator",
        "Only an operator records a human moderator's action (R10.59)",
        "the actor must be named operator:<name>");

    /// <summary>R10.59: a record names who acted, publicly and permanently, so the name after <c>operator:</c> may not be blank.</summary>
    public static Error BlankOperatorName() => new(
        "curia/moderation/blank-operator-name",
        "The operator's name is blank; a record names who acted (R10.59)",
        "the actor must be named operator:<name>, with a <name> that is not blank");

    public static Error NoSuchPost(string postId) => new(
        "curia/moderation/no-such-post",
        "No such post",
        $"post={postId}");

    /// <summary>R10.60: categories and offsets only (R10.27), never the matched value.</summary>
    public static Error RationaleRejected(RiskAnnotations annotations) => RationaleRefusal.Of(
        "curia/moderation/rationale-rejected",
        "The moderator's rationale was rejected by screening; it would land in a public leaf (R10.60)",
        annotations);

    /// <summary>
    /// R10.62: the reason repeats a flag's raiser or rationale. The detail names the field alone —
    /// <c>field=raised_by</c> or <c>field=rationale</c> — never the matched text or the flag, either of
    /// which would disclose what the refusal exists to keep private.
    /// </summary>
    public static Error RationaleDisclosesFlag(string field) => new(
        "curia/moderation/rationale-discloses-flag",
        "The reason repeats a flag's raiser or rationale, which are never published; the reason is (R10.62)",
        $"field={field}");

    /// <summary>A record that would change nothing (spec Decision 11).</summary>
    public static Error NoOp(string postId, ModerationEffect effect, FlagKind category) => new(
        "curia/moderation/no-op",
        "That record would change nothing, and R10.39 counts records",
        $"post={postId} effect={ModerationEffects.Wire(effect)} category={FlagKinds.Wire(category)}");

    /// <summary>R10.61: a restore releases only its own category's hold, and nothing holds the post in this one. Echoes only what the operator supplied.</summary>
    public static Error RestoreOfUnheldCategory(string postId, FlagKind category) => new(
        "curia/moderation/restore-of-unheld-category",
        "Nothing holds the post in that category, so a restore there would release nothing (R10.61)",
        $"post={postId} category={FlagKinds.Wire(category)}");

    /// <summary>R10.61: a dismissal holds and releases nothing, and this category holds the post. Echoes only what the operator supplied.</summary>
    public static Error DismissalOfHeldCategory(string postId, FlagKind category) => new(
        "curia/moderation/dismissal-of-held-category",
        "That category holds the post; a dismissal releases nothing, so restore it or leave the hold (R10.61)",
        $"post={postId} category={FlagKinds.Wire(category)}");
}

/// <summary>
/// R10.62's "published never", where a moderator's own words enter the log: whether a record's
/// reason repeats a raiser, or 32 consecutive characters of a rationale, of a flag on the post.
///
/// <para><b>Compared on derived copies, which are discarded.</b> The reason is refused, never
/// repaired (R10.26); nothing here is written anywhere. Each text is normalized the same way before
/// comparing: every hidden character (<see cref="HiddenCharacters"/>) dropped, every character NFKC
/// cannot take made U+FFFD, then NFKC, lower-cased invariantly, each run of white space collapsed to
/// one space. So a change of case, of spacing or of compatibility form, or a zero-width character
/// inside a raiser, does not get a repeat through; and a noncharacter in a flag, which reaches the
/// append-only private store because a flag's body never passes ADMIT, cannot make every record on
/// its post throw.</para>
///
/// <para><b>A raiser is matched as a whole token, in a form of at least <see cref="RaiserFloor"/>
/// characters without white space</b> — the raiser, and the raiser without its <c>scheme://</c>, each
/// judged on its own. A whole token is an occurrence no character on either side continues as an id:
/// an ASCII letter or digit continues one, and so does a run of the URI unreserved characters
/// <c>-._~</c> that an ASCII letter or digit follows, reading away from the match; anything else,
/// a CJK or accented letter included, is a boundary. Enrolment accepts any non-blank id (D4), and a
/// short id that is itself a word — <c>e</c>, <c>spam</c> — would otherwise refuse every reason using
/// the word and make its post unmoderatable. A raiser below the floor leaves only itself
/// unprotected.</para>
///
/// <para><b>A rationale shorter than <see cref="QuoteLength"/> is not checked</b>, so a one-word
/// rationale such as "spam" never blocks a reason; one at least that long is checked in every window
/// of that length, so quoting part of it counts as quoting it.</para>
/// </summary>
internal static class FlagDisclosure
{
    /// <summary>The shortest run of a rationale that counts as repeating it (R10.60, R10.62).</summary>
    internal const int QuoteLength = 32;

    /// <summary>The shortest raiser form that is checked at all (R10.60, R10.62).</summary>
    internal const int RaiserFloor = 16;

    internal const string RaisedByField = "raised_by";
    internal const string RationaleField = "rationale";

    /// <summary>
    /// The field <paramref name="reason"/> repeats — <see cref="RaisedByField"/> or
    /// <see cref="RationaleField"/> — for any of <paramref name="flagsOnPost"/>, whatever its category
    /// or state; or <see langword="null"/>.
    /// </summary>
    internal static string? Repeated(string reason, IReadOnlyList<RaisedFlag> flagsOnPost, IReadOnlyDictionary<string, string> rationales)
    {
        var said = Normalize(reason);

        foreach (var flag in flagsOnPost)
        {
            var raiser = Normalize(flag.RaisedBy);
            if (NamesAsToken(said, raiser) || NamesAsToken(said, WithoutScheme(raiser)))
                return RaisedByField;
        }

        foreach (var flag in flagsOnPost)
        {
            if (!rationales.TryGetValue(flag.FlagId, out var rationale)) continue;

            var text = Normalize(rationale);
            for (var start = 0; start + QuoteLength <= text.Length; start++)
            {
                if (said.AsSpan().Contains(text.AsSpan(start, QuoteLength), StringComparison.Ordinal))
                    return RationaleField;
            }
        }

        return null;
    }

    /// <summary>
    /// The derived copy every comparison reads. First, every hidden character is dropped and every
    /// character NFKC cannot take becomes U+FFFD: an unpaired surrogate, and every Unicode noncharacter.
    /// Measured on .NET 10's ICU-backed normalization, only U+FFFE among scalar values throws, as
    /// <c>CanonicalJson</c> and <c>JsonReader</c> record; all 66 noncharacters are mapped, since the set
    /// that throws is the platform's and a superset costs nothing on a copy that is discarded. Then NFKC,
    /// lower-cased invariantly, each run of white space collapsed to one space.
    /// </summary>
    internal static string Normalize(string text)
    {
        var total = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.IsBmp && HiddenCharacters.Contains((char)rune.Value)) continue;
            total.Append(IsNoncharacter(rune.Value) ? Rune.ReplacementChar.ToString() : rune.ToString());
        }

        var collapsed = new StringBuilder(total.Length);
        var inWhiteSpace = false;

        foreach (var rune in total.ToString().Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (!inWhiteSpace) collapsed.Append(' ');
                inWhiteSpace = true;
                continue;
            }

            collapsed.Append(Rune.ToLowerInvariant(rune).ToString());
            inWhiteSpace = false;
        }

        return collapsed.ToString();
    }

    /// <summary>A Unicode noncharacter: U+FDD0 to U+FDEF, and the last two code points of every plane (the rule <c>JsonReader.IsNoncharacter</c> applies at ADMIT).</summary>
    private static bool IsNoncharacter(int codePoint) =>
        codePoint is >= 0xFDD0 and <= 0xFDEF || (codePoint & 0xFFFE) == 0xFFFE;

    /// <summary>
    /// Whether <paramref name="said"/> contains <paramref name="form"/> as a whole token: with nothing on
    /// either side that continues it as an id. A form shorter than <see cref="RaiserFloor"/>, or holding
    /// white space, is not checked.
    /// </summary>
    private static bool NamesAsToken(string said, string form)
    {
        if (form.Length < RaiserFloor || form.Contains(' ', StringComparison.Ordinal)) return false;

        for (var at = said.IndexOf(form, StringComparison.Ordinal); at >= 0; at = said.IndexOf(form, at + 1, StringComparison.Ordinal))
        {
            if (!ContinuesAnId(said.AsSpan(0, at), outwardIsLeft: true) && !ContinuesAnId(said.AsSpan(at + form.Length), outwardIsLeft: false))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="beside"/>, the text on one side of a match, continues it as an id: reading
    /// away from the match past any of the URI unreserved characters <c>-._~</c>, the next character is
    /// an ASCII letter or digit. A sentence's full stop, a CJK or accented letter and a space are all
    /// boundaries.
    /// </summary>
    private static bool ContinuesAnId(ReadOnlySpan<char> beside, bool outwardIsLeft)
    {
        for (var i = 0; i < beside.Length; i++)
        {
            var c = outwardIsLeft ? beside[beside.Length - 1 - i] : beside[i];
            if (char.IsAsciiLetterOrDigit(c)) return true;
            if (c is not ('-' or '.' or '_' or '~')) return false;
        }

        return false;
    }

    /// <summary>
    /// The raiser without a leading <c>scheme://</c> — RFC 3986's scheme: a letter, then letters,
    /// digits, <c>+</c>, <c>-</c> or <c>.</c> — or the raiser as it stands.
    /// </summary>
    private static string WithoutScheme(string raiser)
    {
        var separator = raiser.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !char.IsAsciiLetter(raiser[0])) return raiser;

        foreach (var c in raiser.AsSpan(1, separator - 1))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return raiser;
        }

        return raiser[(separator + 3)..];
    }
}
