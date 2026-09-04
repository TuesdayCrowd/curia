namespace Curia.Domain.Content;

/// <summary>
/// Table 9's <c>kind</c> enum: <c>question | answer | finding | comment | revision</c>.
///
/// <para>CS-11 wants closed hierarchies with an explicit <c>Match</c> so a seventh kind breaks
/// every call site. This is an enum rather than a hierarchy because Table 9 makes <c>kind</c> a
/// scalar field on one envelope shape -- the kinds do not carry different fields, they carry
/// different *obligations* (a <c>question</c> requires a title, an <c>answer</c> requires a
/// parent), which live in <see cref="Envelope"/>'s validation rather than in the type. The
/// break-every-call-site property is kept by <see cref="PostKinds.Match{T}"/>.</para>
/// </summary>
public enum PostKind
{
    Question,
    Answer,
    Finding,
    Comment,
    Revision,

    /// <summary>
    /// R8.29 / R8.49 (errata G8, R8.55): an endorsement of a result, carrying the meta-prediction
    /// R15.3 says to collect before anything weights it. The only published carrier of Table 13's
    /// "endorse". Never served to readers before its epoch is sealed (R8.30, R8.50).
    /// </summary>
    Vote,

    /// <summary>
    /// Table 13's V2 and V− (errata G8, R8.56): a reproduction report with evidence, chained to
    /// the result it checked by its envelope digest.
    /// </summary>
    Verification,
}

/// <summary>The wire spellings, and CS-11's exhaustive match.</summary>
public static class PostKinds
{
    /// <summary>Table 9's spellings, plus G8's two. Parsed and rendered here so the wire vocabulary is one list.</summary>
    public static string Wire(PostKind kind) => kind switch
    {
        PostKind.Question => "question",
        PostKind.Answer => "answer",
        PostKind.Finding => "finding",
        PostKind.Comment => "comment",
        PostKind.Revision => "revision",
        PostKind.Vote => "vote",
        PostKind.Verification => "verification",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a Table 9 kind"),
    };

    public static bool TryParse(string wire, out PostKind kind)
    {
        (var ok, kind) = wire switch
        {
            "question" => (true, PostKind.Question),
            "answer" => (true, PostKind.Answer),
            "finding" => (true, PostKind.Finding),
            "comment" => (true, PostKind.Comment),
            "revision" => (true, PostKind.Revision),
            "vote" => (true, PostKind.Vote),
            "verification" => (true, PostKind.Verification),
            _ => (false, default),
        };
        return ok;
    }

    /// <summary>
    /// CS-11's explicit match: an eighth kind added to <see cref="PostKind"/> fails to compile here
    /// and at every call site that uses this instead of a <c>switch</c> with a default arm. The
    /// sixth and seventh did exactly that, which is how every read path learned about them.
    /// </summary>
    public static T Match<T>(
        PostKind kind,
        Func<T> question,
        Func<T> answer,
        Func<T> finding,
        Func<T> comment,
        Func<T> revision,
        Func<T> vote,
        Func<T> verification)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(vote);
        ArgumentNullException.ThrowIfNull(verification);

        return kind switch
        {
            PostKind.Question => question(),
            PostKind.Answer => answer(),
            PostKind.Finding => finding(),
            PostKind.Comment => comment(),
            PostKind.Revision => revision(),
            PostKind.Vote => vote(),
            PostKind.Verification => verification(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a Table 9 kind"),
        };
    }

    /// <summary>
    /// Table 9: <c>title</c> is "Required for <c>question</c>, <c>finding</c>". Stated once, here,
    /// rather than as a condition repeated wherever a title is read.
    /// </summary>
    public static bool RequiresTitle(PostKind kind) =>
        Match(kind, () => true, () => false, () => true, () => false, () => false, () => false, () => false);

    /// <summary>
    /// Table 9: <c>parent</c> is "Thread or post being answered". A <c>question</c> starts a
    /// thread and so has none; every discussion reply names one.
    ///
    /// <para>A <c>revision</c> is included: it revises a specific post, and Table 9's <c>prev</c>
    /// chains the edit history *in addition to* naming what is being revised. Reading <c>prev</c>
    /// as a substitute for <c>parent</c> would leave a revision unattached to any thread.</para>
    ///
    /// <para>A <c>vote</c> and a <c>verification</c> name their subject by <c>target</c> -- the
    /// envelope digest, never a post id (R8.57) -- and are not in any thread, so they carry no
    /// parent.</para>
    /// </summary>
    public static bool RequiresParent(PostKind kind) =>
        Match(kind, () => false, () => true, () => false, () => true, () => true, () => false, () => false);

    /// <summary>
    /// Table 9's <c>body</c> is the content. A vote has none: it is an endorsement and a
    /// meta-prediction, and a member that could only ever be empty would be a member somebody
    /// eventually puts a payload in.
    /// </summary>
    public static bool RequiresBody(PostKind kind) =>
        Match(kind, () => true, () => true, () => true, () => true, () => true, () => false, () => true);

    /// <summary>
    /// R8.55 / R8.56: a <c>vote</c> and a <c>verification</c> name a <c>target</c> digest, and
    /// nothing else does.
    /// </summary>
    public static bool RequiresTarget(PostKind kind) =>
        Match(kind, () => false, () => false, () => false, () => false, () => false, () => true, () => true);

    /// <summary>
    /// The kinds that are discussion -- listed on boards, assembled into threads, searched,
    /// offered by the inbox. A vote and a verification are signals about a result, not the
    /// conversation, and appear on the result's envelope instead (R8.59).
    /// </summary>
    public static bool IsDiscussion(PostKind kind) =>
        Match(kind, () => true, () => true, () => true, () => true, () => true, () => false, () => false);

    /// <summary>
    /// The kinds a reader may be served individually. Everything but a <c>vote</c>, which R8.55
    /// keeps out of every read path until its epoch is sealed -- serving one discloses the tally
    /// R8.30 withholds. It is logged, digest-addressable in the log, and independently verifiable
    /// like any other content; it is not readable back through the Forum.
    /// </summary>
    public static bool IsServedToReaders(PostKind kind) =>
        Match(kind, () => true, () => true, () => true, () => true, () => true, () => false, () => true);

    /// <summary>
    /// Table 13 grades "the result": an <c>answer</c> or a <c>finding</c>. A question asserts
    /// nothing, a comment is by Table 12 "not an answer", a revision is reached through its own
    /// digest, and an endorsement of an endorsement is a ring (R8.58).
    /// </summary>
    public static bool IsResult(PostKind kind) =>
        Match(kind, () => false, () => true, () => true, () => false, () => false, () => false, () => false);
}
