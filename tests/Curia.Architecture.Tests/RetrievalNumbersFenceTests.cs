using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Errata G66, R11.102: the near-duplicate thresholds and the vector floor are a model's, and the table <c>Curia.Application.Retrieval.RetrievalRows</c> is the only place one is written. The fence reads
/// every shipped assembly's IL (the <c>ldc.r8</c> and <c>ldc.r4</c> of its method bodies and the double constants of its metadata) in Debug and in Release (D16), so a number that moved into a
/// constant, a default, or a use site fails the build. The table holds its numbers as integer basis points, so the fence has no exception for it and no other row.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RetrievalNumbersFenceTests
{
    /// <summary>0.94 (R8.18's refusing cosine), 0.85 (the carried annotating cosine), 0.2 (the hashed floor) and 0.64 (the bge floor, 6400 bp).</summary>
    private static readonly double[] Numbers = [0.94, 0.85, 0.2, 0.64];

    /// <summary>Each exception is a reasoned row: the (outermost type, source member) that may hold one of <see cref="Numbers"/> and why. There is none today.</summary>
    private static readonly (string Type, string Member, string Reason)[] Exceptions = [];

    private static bool IsOneOfTheNumbers(double value) =>
        Numbers.Any(n => BitConverter.DoubleToInt64Bits(n) == BitConverter.DoubleToInt64Bits(value) || BitConverter.SingleToInt32Bits((float)n) == BitConverter.SingleToInt32Bits((float)value));

    /// <summary>A literal this assembly's own method holds, for the control below: the scan must find it.</summary>
    private static double AControlLiteral() => 0.94;

    /// <summary>
    /// R11.102, the fence. No type of a <c>src/</c> assembly holds a floating-point literal equal to 0.94, 0.85, 0.2 or 0.64 as a constant or at a use site, other than a row of <see cref="Exceptions"/>.
    /// Two controls: the scan finds the literal 0.94 in this assembly's own method (so a scan that read nothing cannot pass), and finds the table's <c>10000.0</c> divisor in <c>RetrievalRow</c> (so the
    /// table is read and holds its numbers as integers).
    /// </summary>
    [Fact]
    public void R11_102_NoProductionTypeButARowHoldsADedupeOrFloorLiteral()
    {
        var own = IlCallSites.FloatLiterals(typeof(RetrievalNumbersFenceTests).Assembly.Location);
        Assert.Contains(own, l => l.Type == typeof(RetrievalNumbersFenceTests).FullName && l.Member == nameof(AControlLiteral) && IsOneOfTheNumbers(l.Value));

        var shipped = Shipped.ShippedAssemblies();
        var literals = shipped.SelectMany(path => IlCallSites.FloatLiterals(path).Select(l => (Host: Path.GetFileNameWithoutExtension(path), l.Type, l.Member, l.Value))).ToList();
        Assert.Contains(literals, l => l.Type == "Curia.Application.Retrieval.RetrievalRow" && l.Value == 10_000.0);

        var offenders = literals
            .Where(l => IsOneOfTheNumbers(l.Value))
            .Where(l => !Exceptions.Any(e => e.Type == l.Type && e.Member == l.Member))
            .Select(l => $"{l.Host}: {l.Type}.{l.Member} holds {l.Value:R}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(offenders.Count == 0, "a dedupe or retrieval number is written outside RetrievalRows (errata G66, R11.102): " + string.Join("; ", offenders));
        Assert.All(Exceptions, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason), $"{e.Type}.{e.Member} is an exception with no reason"));
    }
}
