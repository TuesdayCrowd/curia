using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Client;

namespace Curia.Client.Cli;

/// <summary>
/// What running the independent verifier established.
///
/// <para><b>The same three outcomes as every other check</b> (<see cref="CheckOutcome"/>), and
/// deliberately not a second enum. This distinction — verified, failed, and <i>could not be
/// checked</i> — is what R6.52 makes normative, and a second spelling of it is a second place for
/// the collapse to reappear. <c>CheckOutcome.CouldNotCheck</c> is what this file used to call
/// <c>Unavailable</c>, and the argument the old name carried is the one written here: "I could not
/// run the second opinion" and "the second opinion says no" are different claims.</para>
/// </summary>
internal sealed record TestisResult(CheckOutcome Outcome, string Description);

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
/// CLI draws between exit 1, a verdict, and exits 2 and 3, which are not one. (3 is the Acta verbs'
/// "could not be checked"; <c>verify</c> does not currently produce it, which is why the arm below
/// reports the code rather than naming a condition it would be guessing at.)</para>
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
            return new TestisResult(CheckOutcome.CouldNotCheck, $"could not stage input files: {ex.Message}");
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
                CheckOutcome.CouldNotCheck,
                $"not run ({ex.Message}). Build it with 'cargo build --bin curia-testis' and point "
                + "$CURIA_TESTIS_BIN at the binary, or put it on PATH. This is a missing second "
                + "opinion, not a failed one.");
        }

        if (process is null)
            return new TestisResult(CheckOutcome.CouldNotCheck, "not run: the process did not start.");

        using (process)
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return process.ExitCode switch
            {
                0 => new TestisResult(
                    CheckOutcome.Verified,
                    "independently verified. " + Compact(stdout)),
                1 => new TestisResult(
                    CheckOutcome.Failed,
                    "INDEPENDENT VERIFICATION FAILED. " + Compact(stderr)),
                _ => new TestisResult(
                    CheckOutcome.CouldNotCheck,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"no verdict from the verifier (exit {process.ExitCode}): {Compact(stderr)}")),
            };
        }
    }

    private static string Compact(string text) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
