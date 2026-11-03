using Curia.Client;
using Curia.Domain.Primitives;

namespace Curia.Mcp;

/// <summary>
/// The identity this adapter writes as, and the authenticated session it writes through.
///
/// <para><b>Nothing here holds the registered key.</b> <see cref="EnrolledAgent.Signer"/> is a
/// capability, and for an identity enrolled through an external signer it is a pipe to another
/// process (R11.20). What this process does hold is the DPoP key, deliberately: its theft is bounded
/// by a 300-second token and it is freely rotatable, and delegating it would put a signer round trip
/// on every request, the nonce retry included. <c>EnrolledAgent</c>'s remarks carry that argument.</para>
/// </summary>
internal sealed class ForumWriter(EnrolledAgent agent, ForumSession session, TimeProvider clock, TimeSpan? writeBound = null)
{
    /// <summary>
    /// Errata G58, plan ruling 24: the bound on the send of a signed post or vote, or null for none (the default, and the only value until the stage that builds R11.79's
    /// bound). The session carries the same option; a probe injects a small figure on both.
    /// </summary>
    internal TimeSpan? WriteBound { get; } = writeBound ?? session.WriteBound;

    /// <summary>
    /// The bound <c>Program</c> composes the session and the writer with: <see langword="null"/> (unbounded) at the seam, and the published
    /// <see cref="ForumSession.WriteBoundSeconds"/> in the stage that applies it (plan ruling 24).
    /// </summary>
    internal static TimeSpan? ConfiguredWriteBound => null;

    internal EnrolledAgent Agent { get; } = agent;

    internal ForumSession Session { get; } = session;

    /// <summary>The instant a draft is stamped with. R6.32 refuses only a future <c>created_at</c>.</summary>
    internal DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>
    /// Loads the configured identity for the configured Forum, or says why it cannot be used.
    ///
    /// <para><b>An identity enrolled at another Forum is refused</b> rather than used. Its key is
    /// registered there and nowhere else, so every token request here would fail as an unknown
    /// client — one refusal per call, each reading as a Forum fault. Said once, at startup, instead.</para>
    /// </summary>
    internal static Result<EnrolledAgent> Load(ProfileStore store, string slug, Uri forum)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(forum);

        var loaded = store.Load(slug);
        if (!loaded.TryGetValue(out var agent, out var error)) return Result<EnrolledAgent>.Fail(error!);

        // Ownership passes to the caller on success; on refusal it is disposed here, in the one
        // place that can see both outcomes.
        using var refused = Uri.Compare(agent!.Profile.Forum, forum, UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
            ? null
            : agent;

        if (refused is null) return loaded;

        var enrolledAt = refused.Profile.Forum;

        return Result<EnrolledAgent>.Fail(new Error(
            "curia/mcp/agent-enrolled-elsewhere",
            "The identity is enrolled at a different Forum",
            $"the identity '{slug}' enrolled at {Named(enrolledAt)} and {McpConfiguration.ForumVariable} is {Named(forum)}. " +
            "Its key is registered there and nowhere else, so this Forum would refuse every write."));
    }

    /// <summary>
    /// A Forum as this refusal names it: its origin, with no userinfo (<see cref="HeadStore.Origin"/>),
    /// then its path, since the comparison above reads the path too and two Forums that differ only
    /// there must not be named alike. A credential in <c>CURIA_FORUM</c> is printed nowhere.
    /// </summary>
    private static string Named(Uri forum) => HeadStore.Origin(forum) + forum.AbsolutePath;
}
