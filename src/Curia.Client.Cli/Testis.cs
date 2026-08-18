using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Client;

namespace Curia.Client.Cli;

internal enum TestisOutcome
{
    /// <summary>The independent verifier confirmed authorship.</summary>
    Verified,

    /// <summary>The independent verifier refused. Exit 1: a failing predicate, named on stderr.</summary>
    Failed,

    /// <summary>The verifier could not be run, or reported a usage error. Not a verdict either way.</summary>
    Unavailable,
}

internal sealed record TestisResult(TestisOutcome Outcome, string Description);

/// <summary>
/// Runs <c>curia-testis</c>, the independently written Rust verifier, over a served post.
///
/// <para><b>Why shell out rather than verify twice in process.</b> Phase 1's exit criterion is
/// that an <i>independently written</i> verifier confirms authorship offline, from the bytes the
/// Forum returns. <c>curia-testis</c> was built in a cleanroom with no access to the C#
/// implementation; a second check inside this process would share <c>Curia.Canon</c>'s
/// canonicalizer with the first and could therefore only ever agree with it. The disagreement is
/// the whole product.</para>
///
/// <para><b>An unavailable verifier is not a verification failure.</b> "I could not run the second
/// opinion" and "the second opinion says no" are different claims, and collapsing them would
/// train a caller to ignore the one that matters -- the same distinction <c>curia-testis</c>'s own
/// CLI draws between its exit codes 1 and 2.</para>
/// </summary>
internal static class Testis
{
    /// <summary>
    /// Where the binary is. <c>$CURIA_TESTIS_BIN</c> names it outright; otherwise <c>curia-testis</c>
    /// is looked up on <c>PATH</c>. No repo-relative guess: this client is installable away from
    /// the checkout, and a path that happened to work in one working directory would fail silently
    /// everywhere else.
    /// </summary>
    private static string Binary =>
        Environment.GetEnvironmentVariable("CURIA_TESTIS_BIN") is { Length: > 0 } configured
            ? configured
            : "curia-testis";

    internal static async Task<TestisResult> RunAsync(
        ProvenancePost post, ReadOnlyMemory<byte> jwksBytes, CancellationToken ct)
    {
        var directory = Directory.CreateTempSubdirectory("curia-verify-");
        try
        {
            var envelopePath = Path.Combine(directory.FullName, "submission.json");
            var jwksPath = Path.Combine(directory.FullName, "jwks.json");

            await File.WriteAllTextAsync(envelopePath, Submission(post), ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(jwksPath, jwksBytes.ToArray(), ct).ConfigureAwait(false);

            return await ExecuteAsync(envelopePath, jwksPath, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new TestisResult(TestisOutcome.Unavailable, $"could not stage input files: {ex.Message}");
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a verification over. It carries
                // only public material: a signed envelope and a published JWKS.
            }
        }
    }

    /// <summary>
    /// The wire submission, rebuilt from what the Forum served: <c>canonical</c> embedded verbatim
    /// as the <c>envelope</c> member, and the detached signature beside it. Embedded verbatim
    /// rather than reserialized -- re-encoding the bytes whose authorship is in question is the
    /// one thing a verification input must not do.
    /// </summary>
    private static string Submission(ProvenancePost post)
    {
        var builder = new StringBuilder();
        builder.Append("{\"envelope\":");
        builder.Append(post.Canonical);
        builder.Append(",\"signature\":");
        builder.Append(JsonSerializer.Serialize(post.Signature));
        builder.Append('}');
        return builder.ToString();
    }

    private static async Task<TestisResult> ExecuteAsync(
        string envelopePath, string jwksPath, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        info.ArgumentList.Add("verify");
        info.ArgumentList.Add("--envelope");
        info.ArgumentList.Add(envelopePath);
        info.ArgumentList.Add("--jwks");
        info.ArgumentList.Add(jwksPath);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new TestisResult(
                TestisOutcome.Unavailable,
                $"not run ({ex.Message}). Build it with 'cargo build --bin curia-testis' and point "
                + "$CURIA_TESTIS_BIN at the binary, or put it on PATH. This is a missing second "
                + "opinion, not a failed one.");
        }

        if (process is null)
            return new TestisResult(TestisOutcome.Unavailable, "not run: the process did not start.");

        using (process)
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return process.ExitCode switch
            {
                0 => new TestisResult(
                    TestisOutcome.Verified,
                    "independently verified. " + Compact(stdout)),
                1 => new TestisResult(
                    TestisOutcome.Failed,
                    "INDEPENDENT VERIFICATION FAILED. " + Compact(stderr)),
                _ => new TestisResult(
                    TestisOutcome.Unavailable,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"usage error from the verifier (exit {process.ExitCode}): {Compact(stderr)}")),
            };
        }
    }

    private static string Compact(string text) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
