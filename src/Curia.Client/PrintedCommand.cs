namespace Curia.Client;

/// <summary>
/// The one definition of "this command acts as an agent" for a command a client prints for the agent to run (errata G59, R11.82 (b)).
///
/// <para>The seam: it returns the command unchanged. The real body appends <c> --agent &lt;slug&gt;</c> as a shell word.</para>
/// </summary>
public static class PrintedCommand
{
    public static string AsAgent(string command, string? slug)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command;
    }
}
