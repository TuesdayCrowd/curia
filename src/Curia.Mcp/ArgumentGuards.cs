using ModelContextProtocol.Protocol;

namespace Curia.Mcp;

/// <summary>
/// R11.43 (errata G29): the guards a tool applies to a value its schema admits but it cannot act on. A string
/// parameter admits <c>""</c> and an array of strings admits <c>[null]</c>; left to the framework, either ends as the
/// SDK's bare invocation error, which names no cause and so gets retried unchanged. Each guard throws the tool's own
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
