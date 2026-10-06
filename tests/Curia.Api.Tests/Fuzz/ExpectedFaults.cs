using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// One known failure the closed pass expects (spec §4.10, "Ledger"). <see cref="Route"/> is
/// <c>"{Method} {Pattern}"</c>; <see cref="Copy"/> is <c>re-signed</c>, <c>unsigned</c> or <c>plain</c>;
/// <see cref="Register"/> is <c>D33-&lt;n&gt;</c>.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "A row exists only while the ledger holds one; an empty ledger is the goal.")]
internal sealed record FaultRow(string Route, string Variant, string Part, string Variation, string Copy, string Register);

/// <summary>
/// The ledger: a ratchet. An observed failure not in it fails the run, a row not observed failing
/// fails the run as stale, and a budget failure may never be a row.
/// </summary>
internal static class ExpectedFaults
{
    /// <summary>The variation prefix of a row the random pass found: <c>random-N</c>, N the draw.</summary>
    internal const string RandomPrefix = "random-";

    internal static readonly ImmutableArray<FaultRow> Rows = [];

    /// <summary>
    /// Whether a row is the random pass's: a random row is judged by the random pass, stale included,
    /// and every other row by the closed pass (review of 9411deb).
    /// </summary>
    internal static bool IsRandom(FaultRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Variation.StartsWith(RandomPrefix, StringComparison.Ordinal);
    }
}
