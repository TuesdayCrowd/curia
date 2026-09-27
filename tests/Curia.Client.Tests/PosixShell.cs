using System.Diagnostics;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// <c>/bin/sh</c>, as an oracle for what a shell makes of a line this client prints (R10.65, errata
/// G17). It knows nothing of the rule it checks, which is the point: a check computed from the
/// implementation would agree with the implementation.
/// </summary>
internal static class PosixShell
{
    /// <summary>What <paramref name="script"/> printed, one line per element; the script must succeed.</summary>
    internal static List<string> Run(string script)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);

        using var shell = Process.Start(start) ?? throw new InvalidOperationException("/bin/sh did not start");
        var stderr = shell.StandardError.ReadToEndAsync();
        var stdout = shell.StandardOutput.ReadToEnd();
        shell.WaitForExit();
        Assert.True(shell.ExitCode == 0, $"/bin/sh exited {shell.ExitCode}: {stderr.Result}");

        return [.. stdout.Split('\n')[..^1]];
    }

    /// <summary>The arguments a shell passes to <c>curia</c> when it runs <paramref name="command"/>.</summary>
    internal static string[] Argv(string command) =>
        [.. Run("curia() { for a in \"$@\"; do printf '%s\\n' \"$a\"; done; }; " + command)];
}
