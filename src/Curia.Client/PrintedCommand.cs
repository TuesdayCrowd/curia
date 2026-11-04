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

    /// <summary>
    /// <paramref name="command"/> ending with <c> --agent</c> and <paramref name="slug"/> as a shell word; with no slug, <c> --agent &lt;name&gt;</c>; with a slug that is not a shell word
    /// (R10.65) or that holds a backtick (which would end the code span several lines print the command in), the command withheld in the published form (errata G59, R11.82 words, a command
    /// withheld), which names no profile above it because the lines that print it print none. A slug with a space or a leading quotation mark is a shell word and is written.
    /// </summary>
    public static string AsAgent(string command, string? slug)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (slug is null) return command + " --agent <name>";

        return !slug.Contains('`', StringComparison.Ordinal) && ShellWord.TryOf(slug, out var word)
            ? command + " --agent " + word
            : command + ", with --agent and the name of the profile you ran as ('curia agents' lists the names; " + Withheld + ")";
    }
}
