using System.Collections.Immutable;
using Curia.Canon.Canonical;

namespace Curia.Canon.Jws;

public sealed record JwsProtectedHeader(
    string Alg, string Kid, string Typ, bool B64, ImmutableArray<string> Crit);

/// <summary>
/// The handle persistence requires: the exact canonical bytes verification consumed.
/// "Store something other than what was verified" has no spelling (R6.12).
/// </summary>
public sealed record VerifiedContent(CanonicalBytes Canonical, JwsProtectedHeader Header);
