using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// CS-9 / R11.3: time enters through TimeProvider, never an ambient clock. Increment 1
/// has no composition root at all, so there is nowhere legitimate for an ambient-clock
/// API to be called from -- unlike a later increment where a composition root might be
/// an allowed exception, here the rule is unconditional: these APIs must appear nowhere
/// in the shipped assemblies' IL, full stop.
///
/// Reads raw PE metadata (member references) rather than reflecting over loaded types,
/// because the properties being banned (DateTimeOffset.UtcNow, DateTime.Now,
/// DateTime.Today) are BCL members Curia code would only ever *call*, never declare --
/// a call site leaves a MemberReference in the calling assembly's metadata even though
/// the property itself is declared in an assembly this test never loads.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test name carries the requirement ID (CS-9) it enforces verbatim (mirrors " +
        "LayeringTests' CS-6/CS-7 precedent).")]
public sealed class BannedApiTests
{
    [Theory]
    [InlineData("Curia.Canon")]
    [InlineData("Curia.Canon.Sodium")]
    [InlineData("Curia.Domain.Primitives")]
    public void CS9_NoAmbientClockApis(string assemblyName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var banned = new[] { "get_UtcNow", "get_Now", "get_Today" };
        var offenders = md.MemberReferences
            .Select(md.GetMemberReference)
            .Select(m => md.GetString(m.Name))
            .Where(name => banned.Contains(name, StringComparer.Ordinal))
            .Distinct()
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{assemblyName} references an ambient clock (CS-9): {string.Join(", ", offenders)}");
    }
}
