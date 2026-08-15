using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// T1.2 / errata A12/R6.31: pins the fix for the two-<c>ServerTimestamp</c>-concepts seam so it
/// cannot silently reopen. <see cref="Curia.Domain.Primitives.ServerTimestamp"/> exists precisely
/// so the Forum's authoritative instant can never be a bare <see cref="DateTimeOffset"/> that a
/// caller could confuse with an envelope's <c>created_at</c> -- but nothing stops a future edit
/// from reintroducing a second, unwrapped <c>ServerTimestamp</c>-named member the way
/// <c>AppendedEvent.ServerTimestamp</c> originally was one. This suite makes that a build failure
/// instead of a seam someone has to notice in review.
///
/// Scans every production hexagon assembly (mirroring <see cref="LayeringTests.HexagonAssemblies"/>'s
/// set, duplicated rather than shared per this suite's file-per-concern, self-contained idiom --
/// see <see cref="EventStoreWriteSurfaceTests"/>'s remarks) for any property, method parameter, or
/// constructor parameter named exactly <c>ServerTimestamp</c>, <c>serverTs</c>, or <c>server_ts</c>
/// -- the three spellings the brief names -- whose type is a bare <see cref="DateTimeOffset"/>
/// (nullable or not). <see cref="Curia.Domain.Primitives.ServerTimestamp.At"/>'s own parameter is
/// deliberately exempt by construction: it is named <c>value</c>, not one of the three flagged
/// spellings, because it is the one legitimate place a bare instant is received and labeled.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test name carries the requirement IDs (A12, R6.31) it enforces verbatim " +
        "(mirrors LayeringTests' CS-6/CS-7 precedent).")]
public sealed class ServerTimestampNamingTests
{
    /// <summary>
    /// The exact three spellings the brief names. Case-sensitive, exact-match, deliberately not
    /// "contains" or "ends with" -- a member named e.g. <c>lastServerTimestampSeen</c> is a
    /// different naming decision this rule has no opinion on; only the three literal tokens are
    /// in scope.
    /// </summary>
    private static readonly string[] FlaggedNames = ["ServerTimestamp", "serverTs", "server_ts"];

    private static (string Name, Assembly Assembly)[] HexagonAssemblies =>
    [
        ("Curia.Canon", typeof(Curia.Canon.Canonical.CanonicalJson).Assembly),
        ("Curia.Canon.Sodium", typeof(Curia.Canon.Sodium.Ed25519Adapter).Assembly),
        ("Curia.Domain.Primitives", typeof(Curia.Domain.Primitives.Result<>).Assembly),
        ("Curia.Domain", typeof(Curia.Domain.DomainEvent).Assembly),
        ("Curia.Application", typeof(Curia.Application.Ports.IEventStore).Assembly),
    ];

    [Fact]
    public void A12_NoServerTimestampNamedMemberIsTypedAsABareDateTimeOffset()
    {
        var offenders = new List<string>();

        foreach (var (assemblyName, assembly) in HexagonAssemblies)
        {
            var types = assembly.GetTypes();
            Assert.NotEmpty(types); // guards against silently scanning an assembly with no types at all

            foreach (var type in types)
            {
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                foreach (var property in type.GetProperties(all))
                {
                    if (IsFlagged(property.Name) && IsBareDateTimeOffset(property.PropertyType))
                        offenders.Add($"{assemblyName}: {type.FullName}.{property.Name} (property)");
                }

                foreach (var method in type.GetMethods(all))
                foreach (var parameter in method.GetParameters())
                {
                    if (IsFlagged(parameter.Name) && IsBareDateTimeOffset(parameter.ParameterType))
                        offenders.Add($"{assemblyName}: {type.FullName}.{method.Name}({parameter.Name}) (parameter)");
                }

                foreach (var ctor in type.GetConstructors(all))
                foreach (var parameter in ctor.GetParameters())
                {
                    if (IsFlagged(parameter.Name) && IsBareDateTimeOffset(parameter.ParameterType))
                        offenders.Add($"{assemblyName}: {type.FullName}..ctor({parameter.Name}) (parameter)");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A member named ServerTimestamp/serverTs/server_ts was typed as a bare DateTimeOffset " +
            "(A12/R6.31: this must be Curia.Domain.Primitives.ServerTimestamp instead, so the " +
            "governing instant can never be confused with an envelope's created_at). Offenders: " +
            string.Join("; ", offenders));
    }

    private static bool IsFlagged(string? name) =>
        name is not null && Array.IndexOf(FlaggedNames, name) >= 0;

    private static bool IsBareDateTimeOffset(Type type) =>
        type == typeof(DateTimeOffset) ||
        Nullable.GetUnderlyingType(type) == typeof(DateTimeOffset);
}
