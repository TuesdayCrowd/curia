using System.Collections;
using Curia.Domain;

namespace Curia.Application.Reads;

/// <summary>
/// A whole log (or a prefix of it) seen as positioned rows: row <i>i</i> is at index <i>i</i> (R6.47). A view over the list, allocating nothing per row, so the folds that take positioned rows
/// are the same folds the whole-log paths run.
/// </summary>
internal sealed class PositionedLog(IReadOnlyList<AppendedEvent> log) : IReadOnlyList<PositionedEvent>
{
    public int Count => log.Count;

    public PositionedEvent this[int index] => new(index, log[index]);

    public IEnumerator<PositionedEvent> GetEnumerator()
    {
        for (var i = 0; i < log.Count; i++) yield return new PositionedEvent(i, log[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
