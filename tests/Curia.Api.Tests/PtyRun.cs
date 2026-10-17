using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Curia.Api.Tests;

/// <summary>
/// What <c>tests/Shared/pty-run.py</c> reports about a command it ran on a pseudo-terminal. Times are milliseconds after the
/// screen was up (after the script's <c>--ready</c> text first appeared), negative before it.
/// </summary>
internal sealed record PtyResult(
    string Status,
    int? Exit,
    int? Signal,
    double? ReadyMs,
    bool? AliveBeforeActions,
    double? ExitedMs,
    IReadOnlyList<(double Ms, string Text)> Chunks,
    string Output);

/// <summary>
/// The pty helper, run as <c>python3 tests/Shared/pty-run.py</c>: a tracked script reached by path and handed to the
/// interpreter, so nothing executable is written at run time and no other test's fork can hold a write descriptor on one
/// (D44, D47). The shipped <c>curia</c> is the command; this is only the other side of its terminal.
/// </summary>
internal static class PtyRun
{
    internal static async Task<PtyResult> RunAsync(IReadOnlyList<string> options, IReadOnlyList<string> command, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the pty helper is a POSIX script run through python3");

        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Script());
        foreach (var option in options) start.ArgumentList.Add(option);
        start.ArgumentList.Add("--");
        foreach (var part in command) start.ArgumentList.Add(part);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("python3 did not start");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var report = await stdout;
        if (process.ExitCode != 0 || report.Length == 0)
            throw new InvalidOperationException($"pty-run.py exited {process.ExitCode}: {await stderr}");

        using var json = JsonDocument.Parse(report);
        var root = json.RootElement;
        double? Number(string name) => root.GetProperty(name) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null;
        var chunks = root.GetProperty("chunks").EnumerateArray()
            .Select(c => (c[0].GetDouble(), Encoding.UTF8.GetString(Convert.FromHexString(c[1].GetString()!))))
            .ToList();

        return new PtyResult(
            root.GetProperty("status").GetString()!,
            root.GetProperty("exit") is { ValueKind: JsonValueKind.Number } exit ? exit.GetInt32() : null,
            root.GetProperty("signal") is { ValueKind: JsonValueKind.Number } signal ? signal.GetInt32() : null,
            Number("ready_ms"),
            root.GetProperty("alive_before_actions") is { ValueKind: JsonValueKind.True or JsonValueKind.False } alive ? alive.GetBoolean() : null,
            Number("exited_ms"),
            chunks,
            Encoding.UTF8.GetString(Convert.FromHexString(root.GetProperty("output_hex").GetString()!)));
    }

    private static string Script()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            var candidate = Path.Combine(at.FullName, "tests", "Shared", "pty-run.py");
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("tests/Shared/pty-run.py was not found above " + AppContext.BaseDirectory);
    }
}
