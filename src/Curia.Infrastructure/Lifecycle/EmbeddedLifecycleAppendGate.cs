using System.Collections.Immutable;
using System.Reflection;
using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Infrastructure.Lifecycle;

/// <summary>
/// R4.38 (add.): the one production <see cref="ILifecycleAppendGate"/>. A kind is enabled only when (i)
/// the approval record embedded in this assembly approves it (lock 1) and (ii) the runtime opt-in
/// <c>Curia:Lifecycle:Enabled</c> names it (lock 2). The shipped record approves nothing, so until the
/// owner lifts the guard the only opt-in that builds a gate is <c>none</c>, and that gate enables nothing.
/// </summary>
public sealed class EmbeddedLifecycleAppendGate : ILifecycleAppendGate
{
    private const string None = "none";

    private readonly ImmutableHashSet<LifecycleKind> _enabled;

    private EmbeddedLifecycleAppendGate(ImmutableHashSet<LifecycleKind> enabled) => _enabled = enabled;

    public bool Enables(LifecycleKind kind) => _enabled.Contains(kind);

    /// <summary>
    /// The only public factory; the Forum's and <c>curia-operator</c>'s compositions call it. It reads the
    /// record through <see cref="LifecycleApprovalRecord.Embedded()"/> itself and names no resource of its
    /// own, so there is exactly one route from it to a record, the one GF6 and the digest fact read.
    /// </summary>
    public static Result<EmbeddedLifecycleAppendGate> FromEmbedded(string? optIn)
    {
        var record = LifecycleApprovalRecord.Embedded();
        if (!record.TryGetValue(out var approval, out var error))
            return Result<EmbeddedLifecycleAppendGate>.Fail(error!);

        return Create(approval, optIn);
    }

    /// <summary>The test seam (FL-11): the record read from <paramref name="resourceName"/>, then <see cref="Create"/>. The public factory does not call it.</summary>
    internal static Result<EmbeddedLifecycleAppendGate> FromEmbedded(Assembly assembly, string resourceName, string? optIn)
    {
        var record = LifecycleApprovalRecord.Embedded(assembly, resourceName);
        if (!record.TryGetValue(out var approval, out var error))
            return Result<EmbeddedLifecycleAppendGate>.Fail(error!);

        return Create(approval, optIn);
    }

    /// <summary>
    /// Lock 2. Checks in this order, refusing by name at the first that fails: (1) the opt-in parses, as
    /// <c>none</c> or a comma list over the three kinds (<c>null</c> is <c>none</c>); (2) it does not enable
    /// rotation or retirement without compromise; (3) it names no kind <paramref name="record"/> does not
    /// approve. Within <c>src/</c> and <c>tools/</c>, called only by <see cref="FromEmbedded(string?)"/>.
    /// </summary>
    internal static Result<EmbeddedLifecycleAppendGate> Create(LifecycleApprovalRecord record, string? optIn)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (ParseOptIn(optIn) is not { } enabled)
            return Result<EmbeddedLifecycleAppendGate>.Fail(LifecycleApprovalErrors.OptInUnreadable());

        if ((enabled.Contains(LifecycleKind.Rotation) || enabled.Contains(LifecycleKind.Retirement))
            && !enabled.Contains(LifecycleKind.Compromise))
        {
            return Result<EmbeddedLifecycleAppendGate>.Fail(LifecycleApprovalErrors.CompromiseOrder("Curia:Lifecycle:Enabled"));
        }

        foreach (var kind in enabled.Order())
        {
            if (!record.Approved.Any(a => a.Kind == kind))
                return Result<EmbeddedLifecycleAppendGate>.Fail(LifecycleApprovalErrors.KindNotApproved(LifecycleApprovalRecord.KindName(kind)));
        }

        return Result<EmbeddedLifecycleAppendGate>.Ok(new EmbeddedLifecycleAppendGate(enabled));
    }

    /// <summary>The kinds an opt-in names, or null when it is not <c>none</c> or a comma list over the three kinds without repeats.</summary>
    private static ImmutableHashSet<LifecycleKind>? ParseOptIn(string? optIn)
    {
        if (optIn is null || string.Equals(optIn, None, StringComparison.Ordinal))
            return [];

        var kinds = ImmutableHashSet.CreateBuilder<LifecycleKind>();
        foreach (var name in optIn.Split(','))
        {
            if (LifecycleApprovalRecord.KindNamed(name) is not { } kind || !kinds.Add(kind))
                return null;
        }

        return kinds.ToImmutable();
    }
}
