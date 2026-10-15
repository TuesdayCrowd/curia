namespace Curia.Client.Tests;

/// <summary>
/// Runs an action with the process's standard streams swapped for writers it owns, and returns what was written.
/// Callers belong to the <c>console</c> collection (<see cref="ConsoleGroup"/>): the streams are shared by every
/// test in the assembly.
/// </summary>
internal static class ConsoleCapture
{
    internal static async Task<(int Exit, string Stdout, string Stderr)> CaptureAsync(Func<Task<int>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var (savedOut, savedError) = (Console.Out, Console.Error);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await action();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedError);
        }
    }
}
