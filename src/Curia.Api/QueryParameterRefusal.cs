using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.29's rules for a query name a route does not read, held in one place so the routes that apply them cannot
/// disagree: the unknown names, sorted ordinally, at most eight named then "and N more", a name echoed only if it is
/// plain, a hint for a near miss, and the list of the names the route reads. Public, as <see cref="SearchQueryParameters"/>
/// is, so a fact reads it without a new <c>InternalsVisibleTo</c> (CS-5).
///
/// <para><b>Task 0's seam, behaviour-preserving.</b> The engine is not extracted yet: this answers null (no refusal) and
/// <see cref="SearchQueryParameters"/> keeps its own body, so the probes fail by assertion and not by a missing type.</para>
/// </summary>
public static class QueryParameterRefusal
{
    /// <summary>Null when every name is in <paramref name="honoured"/>; otherwise the refusal naming those that are not.</summary>
    public static Error? Check(
        IEnumerable<string> names, IReadOnlySet<string> honoured, string type, string title, string routeSentence)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(honoured);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(routeSentence);
        return null;
    }
}
