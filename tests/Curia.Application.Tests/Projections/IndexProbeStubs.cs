using Curia.Application.Projections;

namespace Curia.Application.Tests.Projections;

/// <summary>R9.70: what <c>provenance.revises</c> carries: the kind of the chain root and its post id where the Forum serves it.</summary>
internal sealed record RevisesFact(string Kind, string? PostId);

/// <summary>R9.71 (a): the acceptance that stands for a thread: its instant and, where the post it names has a revision, whether that revision came <c>before_acceptance</c> or <c>after_acceptance</c> in log order (null: none).</summary>
internal sealed record AcceptanceFact(DateTimeOffset AcceptedAt, string? FirstRevision);

/// <summary>R9.71 (b): an answer's relation to its question's first revision. <see cref="Revised"/> false is <c>null</c> on the wire; otherwise <see cref="AnswerPosted"/> is <c>before</c> or <c>after</c>.</summary>
internal sealed record QuestionRevisedFact(bool Revised, string? FirstRevisionDigest, string? FirstRevisionPostId, string? AnswerPosted);

/// <summary>
/// TEST-ONLY STUBS (Task 2, trap 268): the index members R9.69 to R9.71 add do not exist on the tree, so each is an extension method here that answers a value no scene
/// expects, which makes a probe red by its own assertion and not by a compile error. When Task 3 adds the real instance member of the same name and parameters, the instance
/// member wins overload resolution and the stub is dead code; Task 3 then deletes this file. Names and result shapes are the probes' assumption of the build's, and a rename or
/// a different result type is Task 3's to rebind in the probes (it is the only rebinding the build should need).
/// </summary>
internal static class IndexProbeStubs
{
    internal static int ServedAnswersTo(this AcceptedAnswerIndex index, PostView question) => -1;

    internal static RevisesFact? RevisesOf(this AcceptedAnswerIndex index, PostView post) => null;

    internal static AcceptanceFact? AcceptanceOf(this AcceptedAnswerIndex index, PostView question) => null;

    internal static QuestionRevisedFact QuestionRevisedOf(this AcceptedAnswerIndex index, PostView answer) => new(false, null, null, "NOT BUILT");
}
