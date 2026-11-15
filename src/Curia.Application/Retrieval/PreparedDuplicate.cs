using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Domain.Content;
using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>
/// The near-duplicate check after every step at which it can be made to wait (errata G71, R9.65): the envelope's kind, board, text and override, and the index's neighbours of its
/// embedding, over the whole index. It holds no corpus: the caller reads the log after <see cref="DuplicateCheck.PrepareAsync"/> and hands the corpus to
/// <see cref="DuplicateCheck.Assess"/>, which keeps the neighbours that corpus holds. Only <c>PrepareAsync</c> makes one.
/// </summary>
public sealed class PreparedDuplicate
{
    internal PreparedDuplicate(PostKind kind, string board, string text, bool overridden, bool embedded, ImmutableArray<VectorMatch> neighbours)
    {
        Kind = kind;
        Board = board;
        Text = text;
        Overridden = overridden;
        Embedded = embedded;
        Neighbours = neighbours;
    }

    internal PostKind Kind { get; }

    internal string Board { get; }

    internal string Text { get; }

    internal bool Overridden { get; }

    /// <summary>False where the text has no embedding (<c>NoFeatures</c>): nothing was compared, and the assessment is <c>none</c> with no candidates counted, as it always was.</summary>
    internal bool Embedded { get; }

    internal ImmutableArray<VectorMatch> Neighbours { get; }
}
