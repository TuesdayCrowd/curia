namespace Curia.Client;

/// <summary>
/// The one definition of "this command acts as an agent" for a command a client prints for the agent to run (errata G59, R11.82 (b)): where the command is
/// <c>curia resend &lt;digest&gt;</c>, <c>curia acts</c> (with or without <c>--discard &lt;digest&gt;</c>), <c>curia budget</c> or <c>curia flags</c>, the line a client prints ends it with
/// <c> --agent &lt;slug&gt;</c>, the slug a shell word (R10.65). A client that does not know the profile prints <c> --agent &lt;name&gt;</c> and says what <c>&lt;name&gt;</c> is
/// (<see cref="NameSentence"/>). A command that acts as no agent (<c>recheck</c>, <c>read</c>, <c>verify</c>) is never passed through here.
/// </summary>
public static class PrintedCommand
{
    /// <summary>What a line says in place of a command it does not print: a value in it holds a character a shell could act on (R10.65).</summary>
    public const string Withheld = "not written as a command: a value in it holds a character a shell could act on";

    /// <summary>The sentence a line adds when it prints <c>--agent &lt;name&gt;</c>: <c>&lt;name&gt;</c> is not a profile this client knows (R11.82 (b), recorded non-property (g)).</summary>
    public const string NameSentence = "In `--agent <name>`, <name> is the profile that kept the act.";

    /// <summary>What follows a withheld command: the words the errata publishes (R11.82, a command withheld). Prose, never inside a code span.</summary>
    private const string WithheldTail = ", with `--agent` and the name of the profile you ran as (the names are listed by curia agents; " + Withheld + ")";

    /// <summary>
    /// <paramref name="command"/> as a code span (errata G59, R11.82 (b), a command withheld; plan ruling 46): the span holds the command, and for a printed profile ends with <c> --agent</c> and the slug as a shell word; with no slug,
    /// <c> --agent &lt;name&gt;</c>; with a slug that is not a shell word (R10.65) or holds a backtick, the span holds the command without <c>--agent</c> and closes there, and the words that follow it are prose
    /// (<see cref="WithheldTail"/>), never inside the span, where they would read as a command to copy. The span, copied and run on a host of several profiles, refuses (exit 1, nothing sent); on a host of one it acts as the
    /// only profile. A caller adds no backticks of its own.
    /// </summary>
    public static string Span(string command, string? slug)
    {
        ArgumentNullException.ThrowIfNull(command);
        return IsWritten(slug) ? "`" + AsAgent(command, slug) + "`" : "`" + command + "`" + WithheldTail;
    }

    private static bool IsWritten(string? slug) => slug is null || (!slug.Contains('`', StringComparison.Ordinal) && ShellWord.TryOf(slug, out _));

    /// <summary>
    /// <paramref name="command"/> ending with <c> --agent</c> and <paramref name="slug"/> as a shell word; with no slug, <c> --agent &lt;name&gt;</c>; with a slug that is not a shell word
    /// (R10.65) or that holds a backtick, the command withheld in the published form (errata G59, R11.82 words, a command withheld), which names no profile above it because the lines that print it
    /// print none. This is the form for prose that sets no code span of its own (<see cref="Span"/> is the form a line prints a command in). A slug with a space or a leading quotation mark is a shell word and is written.
    /// </summary>
    public static string AsAgent(string command, string? slug)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (slug is null) return command + " --agent <name>";

        return !slug.Contains('`', StringComparison.Ordinal) && ShellWord.TryOf(slug, out var word)
            ? command + " --agent " + word
            : command + WithheldTail;
    }
}
