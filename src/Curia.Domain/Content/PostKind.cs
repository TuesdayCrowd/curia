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
}

/// <summary>The wire spellings, and CS-11's exhaustive match.</summary>
public static class PostKinds
{
    /// <summary>Table 9's spellings. Parsed and rendered here so the wire vocabulary is one list.</summary>
    public static string Wire(PostKind kind) => kind switch
    {
        PostKind.Question => "question",
        PostKind.Answer => "answer",
        PostKind.Finding => "finding",
        PostKind.Comment => "comment",
        PostKind.Revision => "revision",
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
            _ => (false, default),
        };
        return ok;
    }

    /// <summary>
    /// CS-11's explicit match: a sixth kind added to <see cref="PostKind"/> fails to compile here
    /// and at every call site that uses this instead of a <c>switch</c> with a default arm.
    /// </summary>
    public static T Match<T>(
        PostKind kind,
        Func<T> question,
        Func<T> answer,
        Func<T> finding,
        Func<T> comment,
        Func<T> revision)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(revision);

        return kind switch
        {
            PostKind.Question => question(),
            PostKind.Answer => answer(),
            PostKind.Finding => finding(),
            PostKind.Comment => comment(),
            PostKind.Revision => revision(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a Table 9 kind"),
        };
    }

    /// <summary>
    /// Table 9: <c>title</c> is "Required for <c>question</c>, <c>finding</c>". Stated once, here,
    /// rather than as a condition repeated wherever a title is read.
    /// </summary>
    public static bool RequiresTitle(PostKind kind) =>
        Match(kind, () => true, () => false, () => true, () => false, () => false);

    /// <summary>
    /// Table 9: <c>parent</c> is "Thread or post being answered". A <c>question</c> starts a
    /// thread and so has none; everything else is a reply to something.
    ///
    /// <para>A <c>revision</c> is included: it revises a specific post, and Table 9's <c>prev</c>
    /// chains the edit history *in addition to* naming what is being revised. Reading <c>prev</c>
    /// as a substitute for <c>parent</c> would leave a revision unattached to any thread.</para>
    /// </summary>
    public static bool RequiresParent(PostKind kind) =>
        Match(kind, () => false, () => true, () => false, () => true, () => true);
}
