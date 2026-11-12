using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// PR H (errata G67, R6.63; Amendment 3 ruling 35): the operator tool does not read the latest head by shape. <c>ActaLog.LatestHead</c> is the latest head by what an entry
/// looks like, which a holder of the Forum's database credential can write; the signer's growth test and its stop measure from the anchor, the latest head that verifies under the
/// key the tool holds. The Forum serves <c>LatestHead</c> and says <c>signature_valid</c> beside it, so the property is a call site, and the control is that the Forum has one.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorHeadAnchorFenceTests
{
    private const string ActaLogType = "Curia.Application.Projections.ActaLog";
    private const string LatestHeadGetter = "get_LatestHead";

    private static string Assembly(string name) =>
        Shipped.ShippedAssemblies().Single(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.Ordinal));

    private static string[] LatestHeadReaders(string assemblyName) =>
        [.. IlCallSites.CallSites(Assembly(assemblyName))
            .Where(s => s.CalleeType == ActaLogType && s.CalleeMember == LatestHeadGetter)
            .Select(s => $"{s.CallerType}.{s.CallerMethod} -> ActaLog.{s.CalleeMember}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// HSU41. No call site in <c>curia-operator</c> has <c>ActaLog</c> as its callee type and <c>get_LatestHead</c> as its member; the assembly scanned is asserted by name; the control is
    /// that <c>Curia.Api</c> has such a call site, so the scan can see one. Debug and Release: it reads IL. Red on the tip: <c>Curia.OperatorTool.OperatorCommands.SignHeadAsync -&gt; ActaLog.get_LatestHead</c>.
    /// </summary>
    [Fact]
    public void R6_63_HSU41_TheOperatorToolDoesNotReadTheLatestHeadByShape()
    {
        Assert.Equal("curia-operator", Path.GetFileNameWithoutExtension(Assembly("curia-operator")));

        var forum = LatestHeadReaders("Curia.Api");
        Assert.True(forum.Length > 0, "the control: the Forum reads LatestHead (it serves it), so a scan that finds none for it is not reading the call sites");

        var operatorTool = LatestHeadReaders("curia-operator");
        Assert.True(operatorTool.Length == 0, "the operator tool reads the latest head by shape: " + string.Join("; ", operatorTool));
    }
}
