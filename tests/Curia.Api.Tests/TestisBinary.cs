using System.Diagnostics;

namespace Curia.Api.Tests;

/// <summary>
/// Locates and runs <c>curia-testis</c>, the independent Rust verifier.
///
/// <para><b>Fails loudly rather than skipping when it cannot be found.</b> A skipped
/// exit-criterion test reports the same green as a passing one, and this is the criterion Phase 1
/// is defined by -- the single test whose absence would be least noticed and most costly. So a
/// missing binary is a failure with instructions, not a silence.</para>
/// </summary>
internal static class TestisBinary
{
    private const string EnvVarName = "CURIA_TESTIS_BIN";

    /// <summary>
    /// The verifier's path: an explicit override first, then the cargo build output, and finally a
    /// build attempt. Ordered that way so CI can hand over a prebuilt binary and a developer's
    /// machine still works with no setup.
    /// </summary>
    internal static string Locate()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return File.Exists(fromEnv)
                ? fromEnv
                : throw new InvalidOperationException(
                    $"{EnvVarName} is set to '{fromEnv}', which does not exist. An explicit override " +
                    "that points at nothing is worse than none: it looks configured.");
        }

        var crate = FindRepoDirectory("rust/curia-testis");

        foreach (var profile in (string[])["debug", "release"])
        {
            var candidate = Path.Combine(crate, "target", profile, "curia-testis");
            if (File.Exists(candidate)) return candidate;
        }

        return Build(crate);
    }

    private static string Build(string crate)
    {
        var build = Process.Start(new ProcessStartInfo("cargo", "build --bin curia-testis")
        {
            WorkingDirectory = crate,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException(
            "Could not start cargo to build curia-testis. Phase 1's exit criterion is that an " +
            "independently written verifier confirms authorship offline, so this test cannot be " +
            $"satisfied without it. Either install a Rust toolchain or set {EnvVarName} to a " +
            "prebuilt binary.");

        var stderr = build.StandardError.ReadToEnd();
        build.WaitForExit();

        var built = Path.Combine(crate, "target", "debug", "curia-testis");

        return build.ExitCode == 0 && File.Exists(built)
            ? built
            : throw new InvalidOperationException(
                $"cargo build of curia-testis failed (exit {build.ExitCode}):\n{stderr}");
    }

    /// <summary>
    /// Runs <c>curia-testis verify</c>. Exit codes are its published contract: 0 verified,
    /// 1 verification failed, 2 usage error. The caller distinguishes 1 from 2 -- a usage error
    /// dressed as a rejection would make the negative control pass for the wrong reason.
    /// </summary>
    internal static (int ExitCode, string StdOut, string StdErr) Run(
        string binary, string envelopePath, string jwksPath)
    {
        var process = Process.Start(new ProcessStartInfo(
            binary, $"verify --envelope \"{envelopePath}\" --jwks \"{jwksPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"Could not start {binary}.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }

    private static string FindRepoDirectory(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException($"{relative} not found above {AppContext.BaseDirectory}")
            : Path.Combine(dir.FullName, relative);
    }
}
