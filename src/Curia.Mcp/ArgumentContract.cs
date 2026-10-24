using System.Collections.Immutable;
using System.Text.Json;

namespace Curia.Mcp;

/// <summary>What kind of mismatch between a call and the input schema the model was shown (R11.37).</summary>
internal enum ArgumentFaultKind { Unknown, Missing, WrongType }

/// <summary>
/// One place a call does not match its schema. It never carries the received value (R9.25: the value, where
/// naming it echoes content).
/// </summary>
/// <param name="Path">Where in the arguments: <c>board</c>, <c>criteria.kinds</c>, <c>criteria.kinds[0]</c>.</param>
/// <param name="Kind">The mismatch.</param>
/// <param name="Expected">What the schema has there: for an unknown member the members it defines, for a wrong type the types it admits.</param>
/// <param name="Reason">For an unknown member the specification names, why it is not honoured (R9.26).</param>
internal readonly record struct ArgumentFault(string Path, ArgumentFaultKind Kind, string Expected, string? Reason = null);

/// <summary>
/// R11.37 (errata G27, ruling 3): a call checked against the tool's own published input schema.
/// <para><b>This is a stub.</b> It exists so <c>ArgumentContractTests</c> compile and their runtime red is
/// visible; Task 4 of this stage replaces the body. Nothing calls it yet and no server filter is registered.</para>
/// </summary>
internal static class ArgumentContract
{
    internal static ImmutableArray<ArgumentFault> Check(
        JsonElement schema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments, Func<string, string?>? reasonFor = null) =>
        ImmutableArray<ArgumentFault>.Empty;
}
