using Curia.Canon.Json;

namespace Curia.OperatorTool;

/// <summary>
/// Text an agent wrote, made safe to print on an operator's terminal. A flag's rationale is
/// attacker-controlled (R10.35), and an escape sequence or a right-to-left override in it would
/// otherwise rewrite what the moderator sees. This is R10.67's function (<see cref="SpanText"/>), the
/// one a reference reader writes a span with, so the operator's terminal sees the same escapes, in the
/// same form, over the same set (errata G17).
/// </summary>
internal static class TerminalText
{
    /// <summary>A single-line field: line feeds and tabs are escaped too.</summary>
    public static string Line(string text) => SpanText.Line(text);

    /// <summary>A multi-line field: line feeds and tabs survive; every other character in R10.67's set (see <see cref="SpanText"/>) is escaped.</summary>
    public static string Block(string text) => SpanText.Block(text);
}
