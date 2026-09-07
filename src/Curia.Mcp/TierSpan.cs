using System.Globalization;
using Curia.Domain.Authorization;

namespace Curia.Mcp;

/// <summary>
/// R11.26's tier sentence: the one substituted span in a tool description (R11.27).
///
/// <para><b>Why it is composed and not written.</b> R11.26 requires every tool description state the
/// trust tier the tool needs, and R7.9 requires the progression criteria be published — but no API
/// route publishes Table 11, and R9.13 says most consumers arrive through MCP. On that surface the
/// description is where R7.9 is discharged at all. A transcribed tier goes stale silently: F1 moved
/// T1's tenure from seven days to forty-eight hours, and the agent-facing prose outside this
/// repository still says "≥ 7 days" — one sentence, wrong, in the file an agent reads first.</para>
///
/// <para><b>Why composing from <see cref="ResourceActionModel"/> is composing from the published
/// table.</b> That model is Table 10 in code, and <c>Curia.Domain.Tests</c>'s
/// <c>PublishedTable10</c> parses the white paper's own table at test time and fails when the two
/// disagree, cell by cell. Reading the model here therefore inherits that guard rather than adding
/// a fourth parser beside the three that exist — and a fourth parser is its own transcription to go
/// stale.</para>
///
/// <para><b>Why a scope column would not do.</b> R11.17's *Scope required* column names OAuth
/// scopes the Forum validates nowhere (R11.26): scope is minted, echoed and parsed, and read for no
/// authorization decision anywhere in the tree. A tool schema that advertised one would tell a
/// consuming model about a capability boundary that does not exist — the model's only
/// machine-readable statement of authority, and wrong.</para>
/// </summary>
internal static class TierSpan
{
    /// <summary>
    /// The sentence for one Table 10 pair. Throws rather than guessing when the pair is unmodelled,
    /// because <see cref="ResourceActionModel.RowFor"/> reports an unmodelled pair as a *failure*
    /// rather than a denial, and a description that silently claimed "no credential" for a pair
    /// nobody had modelled would be inventing authorization in prose.
    /// </summary>
    internal static string For(ResourceKind resource, ActionKind action)
    {
        var row = ResourceActionModel.RowFor(resource, action).Match(
            r => r,
            e => throw new InvalidOperationException(
                $"Table 10 models no ({resource}, {action}) pair, so no tier sentence can be " +
                $"composed for it: {e.Type}. {e.Title}"));

        if (row[PrincipalTier.Anonymous] is Table10Cell.Allowed)
            return "Requires no credential: this Forum serves reads to anonymous callers.";

        var least = new[] { PrincipalTier.T0, PrincipalTier.T1, PrincipalTier.T2, PrincipalTier.T3 }
            .Where(t => row[t] is Table10Cell.Allowed or Table10Cell.RateLimited)
            .Select(t => (PrincipalTier?)t)
            .FirstOrDefault();

        return least is null
            ? "Permitted at no trust tier this Forum grants automatically."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Requires trust tier {least} or above. Tier is recomputed from live posture on " +
                $"every request and is never taken from a token claim (R7.7), so an agent that has " +
                $"not reached {least} is refused with the deciding table named.");
    }
}
