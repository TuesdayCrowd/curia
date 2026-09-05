using System.Collections.Immutable;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Verification;

namespace Curia.Domain.Retrieval;

/// <summary>The API surfaces R10.2 configures a floor for. An enum, so a surface nobody modelled cannot be served with an implied default.</summary>
public enum RetrievalSurface
{
    /// <summary><c>GET /v1/search</c>.</summary>
    RestSearch,

    /// <summary>The MCP <c>curia_search</c> tool (R10.2 names it; not built before Phase 3 closes, R15.2).</summary>
    McpSearch,
}

public static class RetrievalSurfaces
{
    public static string Wire(RetrievalSurface surface) => surface switch
    {
        RetrievalSurface.RestSearch => "rest-search",
        RetrievalSurface.McpSearch => "mcp-search",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "not a retrieval surface"),
    };

    /// <summary>A surface named in configuration. Anything unmodelled is a failure (R10.<i>n</i>), never a default.</summary>
    public static Result<RetrievalSurface> Parse(string wire) => wire switch
    {
        "rest-search" => Result<RetrievalSurface>.Ok(RetrievalSurface.RestSearch),
        "mcp-search" => Result<RetrievalSurface>.Ok(RetrievalSurface.McpSearch),
        _ => Result<RetrievalSurface>.Fail(RetrievalFloorErrors.UnknownSurface(wire)),
    };
}

/// <summary>
/// R10.2's verification-gated retrieval defaults, as policy rather than as a ranking side effect.
///
/// <para><b>The floor is admission; Table 13's weights are ranking.</b> Both live here, and they
/// are kept apart on purpose: collapsing the floor into a weight is exactly the "ranking side
/// effect" the plan's success criterion forbids, because a weight of zero and a filter look the
/// same on one page and different on the next.</para>
///
/// <para><b>Kind-aware (errata G10).</b> Table 13 grades results, and R8.58 refuses a signal on
/// anything but a servable answer or finding, so a question is V0 forever by construction. A floor
/// evaluated against a level a kind can never hold would hide that kind permanently while
/// reporting a filter. The floor therefore applies only to the kinds Table 13 can grade; the
/// others are subject to the separately stated controls of R10.6 and R10.7, and every response
/// says which kinds the floor applied to.</para>
///
/// <para><b>Published defaults, raised deliberately.</b> R10.2 fixes a value only for the MCP
/// tool; for the REST surface it requires the floor to be configurable and leaves the value to be
/// chosen. It is V0 here, because V1 needs two operator-attested owners (R8.57, R4.30) and a beta
/// Forum has none -- a V1 default today is not a strict gate but an empty result for every query,
/// indistinguishable from an empty corpus. The default rises when V1 becomes reachable, and never
/// above V0 on a default surface before R10.3's discovery channel exists: B1's starvation argument
/// is the reason the pair was adopted together.</para>
/// </summary>
public static class RetrievalFloorPolicy
{
    /// <summary>The published default floor per surface. The table errata G10 carries, in code.</summary>
    public static VerificationLevel PublishedFloor(RetrievalSurface surface) => surface switch
    {
        RetrievalSurface.RestSearch => VerificationLevel.V0,
        RetrievalSurface.McpSearch => VerificationLevel.V1,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "not a retrieval surface"),
    };

    /// <summary>The kinds Table 13 grades and a floor therefore applies to (R8.58).</summary>
    public static ImmutableArray<PostKind> GradableKinds { get; } =
        [.. Enum.GetValues<PostKind>().Where(PostKinds.IsResult)];

    /// <summary>The discussion kinds a floor never applies to, because they cannot reach one.</summary>
    public static ImmutableArray<PostKind> UngradableKinds { get; } =
        [.. Enum.GetValues<PostKind>().Where(k => PostKinds.IsDiscussion(k) && !PostKinds.IsResult(k))];

    public static bool AppliesTo(PostKind kind) => PostKinds.IsResult(kind);

    /// <summary>A floor is V0, V1 or V2. V− is a grade, not a floor: "at least contradicted" means nothing.</summary>
    public static bool IsFloor(VerificationLevel level) => level is VerificationLevel.V0 or VerificationLevel.V1 or VerificationLevel.V2;

    /// <summary>
    /// Whether a post at <paramref name="level"/> passes <paramref name="floor"/>. V0 is "no floor"
    /// and admits everything, contradicted content included -- Table 13 says V− is demoted and
    /// flagged, not hidden. V1 and V2 admit that level and above.
    /// </summary>
    public static bool Admits(VerificationLevel floor, VerificationLevel level) => floor switch
    {
        VerificationLevel.V0 => true,
        VerificationLevel.V1 => level is VerificationLevel.V1 or VerificationLevel.V2,
        VerificationLevel.V2 => level is VerificationLevel.V2,
        VerificationLevel.Contradicted => throw new ArgumentOutOfRangeException(nameof(floor), floor, "V− is a grade, not a floor; see IsFloor"),
        _ => throw new ArgumentOutOfRangeException(nameof(floor), floor, "not a floor; see IsFloor"),
    };

    /// <summary>A post of <paramref name="kind"/> at <paramref name="level"/> is served under <paramref name="floor"/>: the floor is evaluated only for gradable kinds.</summary>
    public static bool Serves(VerificationLevel floor, PostKind kind, VerificationLevel level) =>
        !AppliesTo(kind) || Admits(floor, level);

    /// <summary>Table 13's ranking weights. V3 is unreachable before Phase 4 and has no row here.</summary>
    public static double Weight(VerificationLevel level) => level switch
    {
        VerificationLevel.V0 => 1.0,
        VerificationLevel.V1 => 1.2,
        VerificationLevel.V2 => 2.0,
        VerificationLevel.Contradicted => 0.3,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "not a verification level"),
    };

    /// <summary>A floor named on the wire (<c>min_verification=</c>): V0, V1 or V2, and nothing else.</summary>
    public static Result<VerificationLevel> ParseFloor(string wire) => wire switch
    {
        "V0" => Result<VerificationLevel>.Ok(VerificationLevel.V0),
        "V1" => Result<VerificationLevel>.Ok(VerificationLevel.V1),
        "V2" => Result<VerificationLevel>.Ok(VerificationLevel.V2),
        _ => Result<VerificationLevel>.Fail(RetrievalFloorErrors.NotAFloor(wire)),
    };
}

/// <summary>RFC 9457 problem-type slugs the floor emits.</summary>
public static class RetrievalFloorErrors
{
    public static Error UnknownSurface(string wire) => new(
        "curia/retrieval/unknown-surface",
        "A retrieval floor was configured for a surface this Forum does not model",
        $"surface={wire}; R10.2 makes the floor per surface, and a surface nobody modelled has no floor to apply");

    public static Error NotAFloor(string wire) => new(
        "curia/search/not-a-floor",
        "min_verification must be V0, V1 or V2",
        $"received={wire}; V− is a grade content earns, not a floor a reader can ask for");
}
