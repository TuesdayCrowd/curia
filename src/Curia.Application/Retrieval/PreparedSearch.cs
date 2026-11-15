using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Domain.Retrieval;
using Curia.Domain.Verification;

namespace Curia.Application.Retrieval;

/// <summary>
/// A search after every step at which it can be made to wait (errata G71, R9.65): the query, the floor in force and where it came from, the binding the query's cursor was checked against,
/// and the index's neighbours of the query's embedding, which are over the whole index, withheld posts included. Only <see cref="HybridSearch.PrepareAsync"/> makes one, so
/// <see cref="HybridSearch.Search"/> cannot be handed a hand-built one and cannot be paired with a query it was not prepared for: the prepared search carries its query.
/// A caller reads the log between the two calls, so what it decides on is as new as the wait allows.
/// </summary>
public sealed class PreparedSearch
{
    internal PreparedSearch(
        RetrievalSurface surface,
        SearchQuery query,
        VerificationLevel floor,
        string floorSource,
        bool clamped,
        string binding,
        int limit,
        ImmutableArray<VectorMatch> neighbours)
    {
        Surface = surface;
        Query = query;
        Floor = floor;
        FloorSource = floorSource;
        Clamped = clamped;
        Binding = binding;
        Limit = limit;
        Neighbours = neighbours;
    }

    internal RetrievalSurface Surface { get; }

    internal SearchQuery Query { get; }

    internal VerificationLevel Floor { get; }

    internal string FloorSource { get; }

    internal bool Clamped { get; }

    internal string Binding { get; }

    internal int Limit { get; }

    internal ImmutableArray<VectorMatch> Neighbours { get; }
}
