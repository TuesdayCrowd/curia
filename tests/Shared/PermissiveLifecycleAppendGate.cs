using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;

namespace Curia.Tests.Shared;

/// <summary>
/// R4.38 (add.): the test composition's gate, enabling every lifecycle kind, so tests that append a
/// lifecycle shape against a throwaway store or database run behind it. It lives only here, is linked
/// (never referenced) into the four test assemblies that reach <c>Curia.Application</c>, and is
/// internal, so each holds its own copy and no assembly built from <c>src/</c> or <c>tools/</c> can see
/// one (spec section 4.13).
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Linked into four test assemblies; an assembly that only fences it by name never instantiates it.")]
internal sealed class PermissiveLifecycleAppendGate : ILifecycleAppendGate
{
    public bool Enables(LifecycleKind kind) => true;
}
