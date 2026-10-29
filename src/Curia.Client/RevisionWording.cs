namespace Curia.Client;

/// <summary>
/// R11.61 (errata G45): the words a refusal, a description and a help text share about the Forum's three <c>409</c>s on a submission, in one place
/// so that none of them can edit the words alone. <b>Behaviour-preserving seam of PR 1's Task 0:</b> declared and used by nothing yet; the probes
/// RVO31 to RVO34 and RVO50 read <see cref="Refusal.Summary"/>, a description and a help text against these constants, and are red until each carries
/// them.
/// </summary>
public static class RevisionWording
{
    /// <summary>The problem type of R8.77's second-revision refusal.</summary>
    public const string AlreadyRevisedType = "curia/ingest/already-revised";

    /// <summary>The problem type of R8.77's refusal of a revision whose post another revision is being recorded for.</summary>
    public const string RevisionInFlightType = "curia/ingest/revision-in-flight";

    /// <summary>The problem type of R8.78's refusal of a post identical to a stored one.</summary>
    public const string AlreadyRecordedType = "curia/ingest/already-recorded";

    /// <summary>What <c>already-revised</c> means.</summary>
    public const string AlreadyRevisedCause = "The Forum already holds a revision of this post";

    /// <summary>What to do about <c>already-revised</c>: revise the named post, reading it first, since it may itself have been revised.</summary>
    public const string AlreadyRevisedRemedy = "revise that post instead, and read it first, since it may itself have been revised";

    /// <summary>The other thing <c>already-revised</c> says: the text just sent may be the one already recorded.</summary>
    public const string AlreadyRevisedSameText = "a revision with the text just sent is already recorded";

    /// <summary>What <c>already-recorded</c> means.</summary>
    public const string AlreadyRecordedCause = "The Forum already holds the post just sent";

    /// <summary>What <c>already-recorded</c> did: nothing.</summary>
    public const string NothingStoredOrSpent = "nothing was stored or spent";

    /// <summary>What to do about <c>already-recorded</c>.</summary>
    public const string AlreadyRecordedRemedy = "read it, and do not send it again";

    /// <summary>What <c>revision-in-flight</c> means.</summary>
    public const string InFlightCause = "Another revision of this post is being recorded";

    /// <summary>What to do about <c>revision-in-flight</c>: it may be retried, after reading the post.</summary>
    public const string InFlightRetry = "retry once you have read the post";

    /// <summary>The sentence the description of <c>curia_revise</c> and the help of <c>curia revision</c> carry (R11.61).</summary>
    public const string SecondRevisionRefused = "The Forum refuses a second revision of a post that already has a served one";

    /// <summary>The sentence the descriptions of the four posting tools carry (R11.61).</summary>
    public const string IdenticalPostRefused = "The Forum refuses a post identical to one it has recorded, naming that post";
}
