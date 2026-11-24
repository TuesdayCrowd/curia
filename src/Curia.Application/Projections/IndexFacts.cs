using System.Globalization;

namespace Curia.Application.Projections;

/// <summary>R9.70 (errata G76): what <c>provenance.revises</c> carries: the kind of the chain root, and its post id where the Forum serves it.</summary>
public sealed record RevisesFact(string Kind, string? PostId);

/// <summary>
/// R9.71 (a): the acceptance that stands for a thread: its instant and, where the post it names has a correction, whether that correction came <c>before_acceptance</c> or
/// <c>after_acceptance</c> in log order (null: the post has none).
/// </summary>
public sealed record AcceptanceFact(DateTimeOffset AcceptedAt, string? FirstRevision)
{
    /// <summary>The first correction was recorded before the acceptance event.</summary>
    public const string BeforeAcceptance = "before_acceptance";

    /// <summary>The first correction was recorded after the acceptance event.</summary>
    public const string AfterAcceptance = "after_acceptance";

    /// <summary>
    /// R8.68, R9.71: the instant as the accept route's receipt writes it (round-trip form, invariant culture). The receipt and the question's <c>accepted_at</c> both call this,
    /// so the two cannot be written in two forms.
    /// </summary>
    public static string InstantText(DateTimeOffset instant) => instant.ToString("o", CultureInfo.InvariantCulture);
}

/// <summary>
/// R9.71 (b): an answer's relation to its question's first correction. <see cref="Revised"/> false is <c>null</c> on the wire; otherwise <see cref="AnswerPosted"/> is
/// <c>before</c> or <c>after</c> and the first correction is named by digest and, where the Forum serves it, by post id.
/// </summary>
public sealed record QuestionRevisedFact(bool Revised, string? FirstRevisionDigest, string? FirstRevisionPostId, string? AnswerPosted)
{
    /// <summary>The answer's event precedes the first correction's.</summary>
    public const string Before = "before";

    /// <summary>The answer's event follows the first correction's.</summary>
    public const string After = "after";

    /// <summary>The question has no correction (or the model does not hold the events to order): <c>null</c> on the wire.</summary>
    public static QuestionRevisedFact NotRevised { get; } = new(false, null, null, null);
}
