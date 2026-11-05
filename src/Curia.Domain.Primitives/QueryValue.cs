namespace Curia.Domain.Primitives;

/// <summary>
/// R9.56 (errata G62): whether a query value that is present names nothing, written once for the Forum's route (the refusal and the drop) and for the clients' own
/// test before a request is built, so a client cannot send what the Forum refuses and the two cannot read it differently. The test is
/// <see cref="string.IsNullOrWhiteSpace(string?)"/>, the one R9.54 refuses a blank scalar by; a comma-separated list also names nothing when, with its commas removed, it is blank.
/// </summary>
public static class QueryValue
{
    /// <summary>
    /// True when <paramref name="value"/> is null, empty or white space and, for a list, when it holds nothing but commas and white space
    /// (<c>kind=,</c>, <c>tags=,%20,</c>). <c>a,,b</c> and <c>question,</c> name something and are not refused.
    /// </summary>
    public static bool NamesNothing(string? value, bool isList) =>
        string.IsNullOrWhiteSpace(isList ? value?.Replace(",", string.Empty, StringComparison.Ordinal) : value);
}
