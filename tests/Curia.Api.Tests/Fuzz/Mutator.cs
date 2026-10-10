namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// One part at a time (R14.10): every derived part of an exemplar, every variation of the closed set
/// that applies to it, and for a part a signature covers both copies, re-signed and unsigned.
/// </summary>
internal static class Mutator
{
    internal static IEnumerable<(Part Part, Variation Variation, CopyKind Copy)> Plan(RequestModel exemplar)
    {
        ArgumentNullException.ThrowIfNull(exemplar);
        foreach (var part in exemplar.Parts())
        {
            CopyKind[] copies = exemplar.IsSigned(part) ? [CopyKind.ReSigned, CopyKind.Unsigned] : [CopyKind.Plain];
            foreach (var variation in Variations.Closed)
            {
                if (!variation.Applies(part)) continue;
                foreach (var copy in copies)
                    yield return (part, variation, copy);
            }
        }
    }
}
