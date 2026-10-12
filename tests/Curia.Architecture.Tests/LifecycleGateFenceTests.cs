using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Infrastructure.Lifecycle;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R4.38 (add.), spec section 4.13: GF6 and the public factory's tripwire. CP-B forbids every PR after
/// the guard's own to touch this file; it goes red, deliberately, only in the owner's lift PR.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleGateFenceTests
{
    private static readonly LifecycleKind[] Kinds = [LifecycleKind.Rotation, LifecycleKind.Retirement, LifecycleKind.Compromise];

    /// <summary>GF6: the record embedded in the production assembly is present, parses, and approves nothing.</summary>
    [Fact]
    public void R4_38_TheApprovedSetIsEmpty()
    {
        var record = LifecycleApprovalRecord.Embedded();

        Assert.True(record.IsOk, record.Match(_ => string.Empty, e => $"{e.Type}: {e.Detail}"));
        Assert.Empty(record.Match(r => r.Approved, _ => default));
    }

    /// <summary>
    /// GF6's production-path tripwire: the gate the Forum and <c>curia-operator</c> run is built by the
    /// public <see cref="EmbeddedLifecycleAppendGate.FromEmbedded(string?)"/>, so this fact reads that
    /// path. Each opt-in passes the parse and the compromise-order rule, so only the approval check can
    /// refuse it. Red when the public factory reads a record that approves a kind (FL-23).
    /// </summary>
    [Fact]
    public void R4_38_ThePublicFactoryRefusesEachKindItIsAsked()
    {
        foreach (var optIn in new[] { "compromise", "compromise,rotation", "compromise,retirement" })
        {
            var refused = EmbeddedLifecycleAppendGate.FromEmbedded(optIn);
            Assert.False(refused.IsOk, $"the public factory built a gate for {optIn}");
            Assert.Equal(LifecycleApprovalErrors.KindNotApprovedType, refused.Match(_ => string.Empty, e => e.Type));
        }

        var none = EmbeddedLifecycleAppendGate.FromEmbedded("none");
        Assert.True(none.IsOk);
        var gate = none.Match(g => g, e => throw new InvalidOperationException(e.Type));
        foreach (var kind in Kinds)
            Assert.False(gate.Enables(kind), $"the opt-in none enables {kind}");
    }
}
