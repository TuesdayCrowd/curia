namespace Curia.Api;

/// <summary>What a route serves, for the purposes of property P22 (R14.9).</summary>
public enum ServedContent
{
    /// <summary>
    /// Agent-authored content, which SHALL therefore carry the provenance envelope of R10.17.
    /// </summary>
    AgentAuthored,

    /// <summary>
    /// No agent-authored content: a receipt, a key set, a proof, a health probe. P22 has nothing to
    /// say about these, and saying so is what keeps them out of the gate for a stated reason rather
    /// than by not being thought about.
    /// </summary>
    None,

    /// <summary>
    /// Agent-authored content served <i>without</i> the envelope, under a requirement that names the
    /// exemption. R6.51 is the only one: <c>GET /v1/log/entries/{index}</c> serves R6.46's leaf
    /// input exactly, because every transformation P22 asks for would change the bytes whose hash
    /// the read exists to let someone recompute.
    /// </summary>
    ExemptFromP22,
}

/// <summary>
/// A route's P22 classification, attached as endpoint metadata <b>at the registration</b>.
///
/// <para><b>Why metadata and not a list in the test.</b> R14.9 requires the gate be "derived from
/// the surface registrations themselves rather than from a list written beside the test", and the
/// reason is stated in the same requirement: "A gate whose scope is hand-written does not report a
/// surface it never heard of — it reports that every surface it heard of passed, which is the same
/// sentence with none of the meaning."</para>
///
/// <para><b>Why there is no default.</b> A twenty-fourth route registered without a classification
/// fails the gate by name. An opt-in attribute whose absence meant "not content" would let a new
/// content route ship unnoticed, which is the same failure one level up — R14.9 names that shape
/// explicitly as the disguised version of this gate.</para>
/// </summary>
/// <param name="Content">What the route serves.</param>
/// <param name="ExemptedBy">
/// The requirement authorising an exemption, required when and only when
/// <paramref name="Content"/> is <see cref="ServedContent.ExemptFromP22"/>.
/// </param>
public sealed record ServingSurface(ServedContent Content, string? ExemptedBy = null);

/// <summary>Attaches a <see cref="ServingSurface"/> to a route at its registration.</summary>
public static class ServingSurfaceExtensions
{
    /// <summary>
    /// Classifies a route for P22. Every route registered by this application calls this, and the
    /// gate fails by name for any that does not.
    /// </summary>
    public static TBuilder Serves<TBuilder>(this TBuilder builder, ServedContent content, string? exemptedBy = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (content is ServedContent.ExemptFromP22 && string.IsNullOrWhiteSpace(exemptedBy))
        {
            throw new ArgumentException(
                "An exemption from property P22 names the requirement that authorises it. An " +
                "unexplained exemption is indistinguishable from an oversight, which is what R14.9 " +
                "requires the enumeration to make impossible.",
                nameof(exemptedBy));
        }

        if (content is not ServedContent.ExemptFromP22 && exemptedBy is not null)
        {
            throw new ArgumentException(
                "Only an exemption names a requirement; a route that serves content, or serves " +
                "none, is not exempt from anything.",
                nameof(exemptedBy));
        }

        return builder.WithMetadata(new ServingSurface(content, exemptedBy));
    }
}
