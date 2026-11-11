using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// PR H (errata G67, R6.62, R12.18): the two fences that keep the declaration key a key of its own. HSU1: no log key is a statement signer. HSU6: every
/// <c>LogEntries</c> event-type constant is either classified by the lifecycle guard or listed here with its reason, so that a new <c>log.&lt;anything&gt;</c>
/// is a build failure until someone decides. Both read the types as built; the assemblies scanned are asserted by name so that a fence over nothing is red.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DeclarationKeyFenceTests
{
    /// <summary>
    /// HSU1. The log key signs heads and is what an unattended timer holds, so it must not also be a signer of compromise statements; the declaration key is the
    /// only <see cref="ILifecycleStatementSigner"/> in the two assemblies that could hold one. Red on the seam, where <c>LogSigningKey</c> still implements the interface.
    /// </summary>
    [Fact]
    public void R6_62_HSU1_NoLogKeyIsAStatementSigner()
    {
        var scanned = new[] { typeof(LogSigningKey).Assembly, typeof(ILifecycleStatementSigner).Assembly };
        Assert.Equal(["curia-operator", "Curia.Application"], scanned.Select(a => a.GetName().Name!).ToArray());

        Assert.DoesNotContain(typeof(ILifecycleStatementSigner), typeof(LogSigningKey).GetInterfaces());
        Assert.Null(typeof(LogSigningKey).GetMethod("SignStatement"));

        var implementers = scanned
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsInterface && typeof(ILifecycleStatementSigner).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Curia.OperatorTool.DeclarationSigningKey"], implementers);
    }

    /// <summary>
    /// What a <c>LogEntries</c> type constant may be without the guard classifying it, each with its reason (Amendment 2 ruling 29). Three rows: heads are not
    /// lifecycle; a log key's publication is the signer's own R6.50 act and grants no declaration power; and publishing a declaration key ends no agent's key
    /// (honouring a declaration under it is the guarded compromise act), and the owner must publish it before the hosted instance is announced.
    /// </summary>
    private static readonly Dictionary<string, string> Unclassified = new(StringComparer.Ordinal)
    {
        [nameof(LogEntries.HeadType)] = "a signed head is not a lifecycle act",
        [nameof(LogEntries.KeyType)] = "a log key's publication is the signer's own R6.50 act and grants no declaration power after errata G67",
        [nameof(LogEntries.DeclarationKeyType)] = "publishing a key ends no agent's key; honouring a declaration under it is the guarded compromise act, and the first-published slot must be closed by the owner before hosting",
    };

    /// <summary>
    /// HSU6. Control on the seam (the seam declares the constant and classifies nothing); red under FHSU6, where <c>Classify</c> is made to return a kind for a
    /// <c>log.*</c> type or a fourth constant is added with no decision.
    /// </summary>
    [Fact]
    public void R4_38_HSU6_EveryLogEntryTypeIsClassifiedOrListedWithAReason()
    {
        var types = typeof(LogEntries)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Type", StringComparison.Ordinal))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

        Assert.Contains(nameof(LogEntries.HeadType), types.Keys);
        Assert.Contains(nameof(LogEntries.KeyType), types.Keys);
        Assert.Contains(nameof(LogEntries.DeclarationKeyType), types.Keys);

        var problems = new List<string>();
        foreach (var (name, type) in types)
        {
            var classified = LifecycleAppendGuard.Classify(AggregateVersion.From(1).Match(v => v, _ => throw new InvalidOperationException()), [EventOf(type)]).Count > 0;
            var listed = Unclassified.ContainsKey(name);
            if (!classified && !listed) problems.Add($"{name} ({type}) is neither classified by the lifecycle guard nor listed with a reason");
            if (classified && listed) problems.Add($"{name} ({type}) is classified and also listed as unclassified");
        }

        foreach (var name in Unclassified.Keys.Where(n => !types.ContainsKey(n)))
            problems.Add($"the allowlist names {name}, which LogEntries no longer declares");

        Assert.Equal(3, Unclassified.Count);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static DomainEvent EventOf(string type) => new(
        EventId.Create("evt-fence").Match(v => v, e => throw new InvalidOperationException(e.Type)),
        EventType.Create(type).Match(v => v, e => throw new InvalidOperationException(e.Type)),
        ActorId.Create("operator:ops").Match(v => v, e => throw new InvalidOperationException(e.Type)),
        new Curia.Canon.Json.JsonValue.Object([]));
}
