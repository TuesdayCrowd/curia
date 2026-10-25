using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// One line of a recheck (errata G35, R9.34): the word it begins with, the line as this client words it, and what the line
/// means for a caller's exit. <see cref="Text"/> is this client's own words around display literals (R10.63).
/// </summary>
/// <param name="State">The word the line begins with: <c>current</c>, <c>superseded</c>, <c>withheld</c>, <c>unknown</c>, <c>malformed</c> or <c>disputed</c>, or the Forum's own word for a state this build does not know.</param>
/// <param name="Text">The line, without its newline.</param>
/// <param name="Gone">The citation is withheld, unknown or in a state this build does not know: change what is cited.</param>
/// <param name="Malformed">The element was not a digest.</param>
/// <param name="Disputed">The Forum carries the disputed mark (R6.57) on the post it serves for this element.</param>
/// <param name="Failed">The digest computed from the served canonical form is not the digest asked about (R9.34).</param>
public sealed record CitationLine(string State, OwnText Text, bool Gone, bool Malformed, bool Disputed, bool Failed);

/// <summary>
/// What <see cref="CitationLines.For"/> made of a batch response: one line per element sent, or an error and no line when the
/// response answers other than what was sent (R9.34).
/// </summary>
/// <param name="Lines">One line per element, in the order sent; empty when <see cref="Error"/> is set.</param>
/// <param name="Error">Why the response was refused, or <see langword="null"/>.</param>
public sealed record CitationReport(ImmutableArray<CitationLine> Lines, Error? Error)
{
    /// <summary>True when any line is FAILED.</summary>
    public bool Failed => Lines.Any(line => line.Failed);
}

/// <summary>
/// R9.34 (errata G35): every word a recheck line uses, in one place, so <c>curia recheck</c> and <c>curia_recheck</c> compose
/// from the same definition and no form edits its words alone (<see cref="Supersession"/>'s lesson).
///
/// <para><b>Seam, Task 0 of PR 2b.</b> This is the verb's loop as it stood, moved here without a change of word; it checks
/// neither what the response's length is nor which digest sits where, and names neither a post id nor a successor's.
/// Task 1 replaces it.</para>
/// </summary>
public static class CitationLines
{
    /// <summary>The lines for <paramref name="items"/>, the Forum's answer to a batch of <paramref name="sent"/>.</summary>
    public static CitationReport For(IReadOnlyList<string> sent, ImmutableArray<CitationDocument> items)
    {
        ArgumentNullException.ThrowIfNull(sent);

        var lines = ImmutableArray.CreateBuilder<CitationLine>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            switch (item.State)
            {
                case "current" when item.Post?.Provenance.DisputedByLogIndex is not null:
                    lines.Add(new CitationLine("disputed", Of($"disputed    {item.Digest}  the signature may hold, but the key was declared compromised from before this post (the Forum's word, R6.57): do not cite it as the author's"), false, false, true, false));
                    break;

                // A successor may be signed under another key, so the line still names what to read next.
                case "superseded" when item.Post?.Provenance.DisputedByLogIndex is not null:
                    lines.Add(new CitationLine("disputed", Of($"disputed    {item.Digest}  (superseded -> {Literals(item.Successors)}) the signature may hold, but the key was declared compromised from before this post (the Forum's word, R6.57): do not cite it as the author's; re-read the successor before citing it"), false, false, true, false));
                    break;

                case "current":
                    lines.Add(new CitationLine("current", Of($"current     {item.Digest}  cite as-is"), false, false, false, false));
                    break;

                case "superseded":
                    lines.Add(new CitationLine("superseded", Of($"superseded  {item.Digest}  -> {Literals(item.Successors)}{new OwnText(item.Forked ? "  (forked: more than one revision chains here)" : string.Empty)}  re-read before citing; the original still stands"), false, false, false, false));
                    break;

                case "withheld":
                    lines.Add(new CitationLine("withheld", Of($"withheld    {item.Digest}  drop this citation; it is no longer served (withholding can be reversed -- re-check later)"), true, false, false, false));
                    break;

                case "unknown":
                    lines.Add(new CitationLine("unknown", Of($"unknown     {item.Digest}  no post here bears this digest; check the encoding (sha256:<64 hex>), then drop it"), true, false, false, false));
                    break;

                case "malformed":
                    lines.Add(new CitationLine("malformed", Of($"malformed   [#{i + 1}]  not a digest; expected sha256:<64 lowercase hex>"), false, true, false, false));
                    break;

                default:
                    lines.Add(new CitationLine(item.State, Of($"{item.State}  {item.Digest}  (a state this build does not know; treat the citation as changed)"), true, false, false, false));
                    break;
            }
        }

        return new CitationReport(lines.MoveToImmutable(), null);
    }

    private static OwnText Of(FrameText line) => new(new FrameBuilder().Append(line).ToString());

    /// <summary>A served list, each element a display literal, joined by commas (R10.63, errata G17).</summary>
    private static OwnText Literals(IEnumerable<string> values) =>
        new(string.Join(", ", values.Select(DisplayLiteral.Of)));
}
