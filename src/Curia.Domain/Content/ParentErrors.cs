using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Domain.Content;

/// <summary>
/// R8.70 (errata G38): the two refusals of a submission whose <c>parent</c> is not a post of the kind its own kind needs. They
/// belong to the log, not to the envelope's own content, so their types sit beside <c>curia/ingest/unstorable-member</c> and not
/// under <see cref="ContentErrors.TypePrefix"/>; the submit route maps them, <c>VerifyRefusals.StatusOf</c> does not.
/// </summary>
public static class ParentErrors
{
    /// <summary>R8.70: no post has the id the submission names as its parent.</summary>
    public const string NotFoundType = "curia/ingest/parent-not-found";

    /// <summary>R8.70: an answer names a post of another kind than <c>question</c>.</summary>
    public const string NotAQuestionType = "curia/ingest/parent-not-a-question";

    public const string NotFoundTitle = "The parent is not a post";

    public const string NotAQuestionTitle = "An answer's parent must be a question";

    /// <summary>R8.72 (errata G41): the parent exists and moderation does not serve it (withheld or quarantined).</summary>
    public const string WithheldType = "curia/ingest/parent-withheld";

    public const string WithheldTitle = "The parent is not being served";

    /// <summary>R9.42 (errata G41): <c>GET /v1/posts/{id}</c> for a post that exists and is not served.</summary>
    public const string PostWithheldType = "curia/posts/withheld";

    /// <summary>R9.42: <c>GET /v1/posts/{id}</c> for an id no post bears (and for a signal kind, which is no post a reader is served).</summary>
    public const string PostNotFoundType = "curia/posts/not-found";

    /// <summary>The detail's stand-in for a parent that is not shaped as a post id, so no text a caller wrote reaches a response.</summary>
    public const string NotAPostId = "(not a post id)";

    /// <summary>R8.70: the parent is echoed only where it is shaped as a post id.</summary>
    public static Error NotFound(string? parent) => new(NotFoundType, NotFoundTitle, "parent=" + Echo(parent));

    /// <summary>R8.70: names the parent and its stored kind.</summary>
    public static Error NotAQuestion(string? parent, PostKind kind) =>
        new(NotAQuestionType, NotAQuestionTitle, $"parent={Echo(parent)} kind={PostKinds.Wire(kind)}");

    /// <summary>R8.72: names the parent, echoed under the same rule as the other two.</summary>
    public static Error Withheld(string? parent) => new(WithheldType, WithheldTitle, "parent=" + Echo(parent));

    private static string Echo(string? parent) =>
        PostId.Parse(parent).TryGetValue(out var id, out _) ? id!.Value : NotAPostId;
}
