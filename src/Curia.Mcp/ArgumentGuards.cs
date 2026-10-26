using Curia.Domain.Primitives;
using ModelContextProtocol.Protocol;

namespace Curia.Mcp;

/// <summary>
/// R11.43 (errata G29): the guards a tool applies to a value its schema admits but it cannot act on. A string
/// parameter admits <c>""</c> and an array of strings admits <c>[null]</c>; left to the framework, either ends as the
/// SDK's bare invocation error, which names no cause and so gets retried unchanged. Each guard returns the tool's own
/// refusal in the voice of G27 ruling 3, <c>NOT SENT</c>, naming the argument (and the item's index) and never a
/// value, so a model that sent a blank is not shown its blank back (R10.63). A guard answers a result with
/// <c>isError</c> set and not an exception, because the SDK puts its own prefix before an exception's message and a
/// refusal that does not begin <c>NOT SENT</c> is not the one the model was promised (R11.37's contract refusal
/// is returned for the same reason).
/// </summary>
internal static class ArgumentGuards
{
    /// <summary>An identifier a caller chose is neither null, empty nor whitespace.</summary>
    internal static CallToolResult? Identifier(string? value, string argument)
    {
        return string.IsNullOrWhiteSpace(value) ? Refusal($"{argument} is empty or blank") : null;
    }

    /// <summary>
    /// R11.45: an optional value that is present is not blank. Absent (null) is the argument not given and is no refusal; an
    /// empty or whitespace string is a value the model meant and the tool cannot honour, so it is refused rather than read as
    /// no filter or no pin (D68).
    /// </summary>
    internal static CallToolResult? PresentValue(string? value, string argument)
    {
        return value is not null && string.IsNullOrWhiteSpace(value) ? Refusal($"{argument} is empty or blank") : null;
    }

    /// <summary>
    /// R11.52 (errata G40): a pin that is present is in the published form, <c>sha256:</c> and 64 lowercase hex (<see cref="EnvelopeDigest.IsPrefixedForm"/>).
    /// Absent (null) is no pin. A pin is a statement about a document and a digest has one spelling, so any other form is refused by name and is
    /// not folded; the refusal states the form and never echoes the value (R10.63).
    /// </summary>
    internal static CallToolResult? DigestForm(string? value, string argument)
    {
        return value is not null && !EnvelopeDigest.IsPrefixedForm(value)
            ? Refusal($"{argument} is not a digest in the published form (sha256: and 64 lowercase hexadecimal digits)")
            : null;
    }

    /// <summary>R11.49: an array argument that must hold at least one item is not empty. An absent array (null) is left to the contract check.</summary>
    internal static CallToolResult? NotEmpty(IReadOnlyCollection<string?>? items, string argument)
    {
        return items is { Count: 0 } ? Refusal($"{argument} is empty") : null;
    }

    /// <summary>An item of an array argument is not null. <paramref name="blankToo"/> also refuses an empty or whitespace item.</summary>
    internal static CallToolResult? Items(IEnumerable<string?>? items, string argument, bool blankToo = false)
    {
        if (items is null) return null;

        var index = 0;
        foreach (var item in items)
        {
            if (item is null || (blankToo && string.IsNullOrWhiteSpace(item)))
                return Refusal($"{argument}[{index}] is {(item is null ? "null" : "empty or blank")}");
            index++;
        }

        return null;
    }

    private static CallToolResult Refusal(string what) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = $"NOT SENT: {what}. Nothing was sent." }] };
}
