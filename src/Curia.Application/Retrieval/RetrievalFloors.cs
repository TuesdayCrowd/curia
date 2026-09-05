using System.Collections.Immutable;
using Curia.Domain.Primitives;
using Curia.Domain.Retrieval;
using Curia.Domain.Verification;

namespace Curia.Application.Retrieval;

/// <summary>
/// R10.2's per-surface floors as this deployment runs them: the published defaults
/// (<see cref="RetrievalFloorPolicy.PublishedFloor"/>) with configuration laid over them. A
/// surface or level configuration cannot name is a failure at startup (CS-17), never a default.
/// </summary>
public sealed record RetrievalFloors(ImmutableDictionary<RetrievalSurface, VerificationLevel> Configured)
{
    public static RetrievalFloors Published { get; } = new(ImmutableDictionary<RetrievalSurface, VerificationLevel>.Empty);

    /// <summary>The floor in force for a surface, and whether configuration or the published table supplied it.</summary>
    public (VerificationLevel Floor, string Source) Resolve(RetrievalSurface surface) =>
        Configured.TryGetValue(surface, out var configured)
            ? (configured, "configured")
            : (RetrievalFloorPolicy.PublishedFloor(surface), "published");

    /// <summary>Parses <c>surface=level</c> pairs from configuration; every entry must name a modelled surface and a floor.</summary>
    public static Result<RetrievalFloors> Parse(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var configured = ImmutableDictionary.CreateBuilder<RetrievalSurface, VerificationLevel>();
        foreach (var (surfaceWire, levelWire) in entries)
        {
            if (!RetrievalSurfaces.Parse(surfaceWire).TryGetValue(out var surface, out var surfaceError))
                return Result<RetrievalFloors>.Fail(surfaceError!);
            if (!RetrievalFloorPolicy.ParseFloor(levelWire ?? string.Empty).TryGetValue(out var level, out var levelError))
                return Result<RetrievalFloors>.Fail(levelError!);
            configured[surface] = level;
        }

        return Result<RetrievalFloors>.Ok(new RetrievalFloors(configured.ToImmutable()));
    }
}
