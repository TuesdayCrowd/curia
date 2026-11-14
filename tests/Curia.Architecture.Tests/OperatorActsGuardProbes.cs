using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Acta;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// PR N2 (errata G70, R6.66), Task 2: OAS18, which names <c>LogEntries.OperatorKeyType</c> (added by Task 2; Task 1 kept the file as <c>.cs.pending</c> because a test file naming a type that does not exist breaks the
/// whole assembly's build). The string it pins; the real discharge is HSU6 (<c>DeclarationKeyFenceTests</c>), which fails on the constant until a row decides it.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActsGuardProbes
{
    /// <summary>OAS18: <c>OperatorKeyType</c> is classified by the guard or listed in <c>Unclassified</c> with a reason.</summary>
    [Fact]
    public void OAS18_TheOperatorKeyTypeIsDecidedByTheGuard()
    {
        Assert.Equal("log.operator-key", LogEntries.OperatorKeyType);
        // The row in DeclarationKeyFenceTests.Unclassified (HSU6, with its reason) and its count of 4 are the guard's decision; this probe is the string they pin.
    }
}
