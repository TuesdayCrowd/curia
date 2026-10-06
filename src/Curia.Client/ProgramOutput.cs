using System.Diagnostics;
using System.Text;

namespace Curia.Client;

/// <summary>
/// What another program wrote, read as R10.64 (errata G17) says: its bytes decoded as UTF-8, each
/// ill-formed sequence replaced by U+FFFD under the Unicode Standard's maximal-subpart rule. Every
/// child process this library or its clients run -- <c>curia-testis</c>, an external signer -- is
/// read through here, and an architecture fact holds every other read of a process's output to be
/// none (Task 10's review).
///
/// <para><b>Why bytes, and not the process's reader.</b> A redirected <see cref="Process"/> hands
/// back a <see cref="StreamReader"/> that detects a byte order mark at offset 0 of the stream, and
/// setting <see cref="ProcessStartInfo.StandardOutputEncoding"/> leaves the detection on. So the
/// child's first bytes chose the decoding, not the rule: on macOS a stream that began <c>FF FE</c>
/// was read as UTF-16LE, and a leading <c>EF BB BF</c> was dropped. A verifier's verdict or a
/// signer's refusal is shown to a reader, and what it shows must not depend on what the other
/// program put first. Reading the raw streams and decoding them here is the only form in which no
/// reader detects anything.</para>
/// </summary>
public static class ProgramOutput
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary><paramref name="bytes"/> as UTF-8, a byte order mark kept as U+FEFF and never read as a choice of decoding.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Utf8.GetString(bytes);
    }

    /// <summary>Both of <paramref name="process"/>'s redirected streams, read together so neither fills while the other is waited on.</summary>
    public static async Task<(string Output, string Error)> ReadAsync(Process process, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);

        var output = ReadAllAsync(process.StandardOutput.BaseStream, ct);
        var error = ReadAllAsync(process.StandardError.BaseStream, ct);
        return (await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    /// <summary>The same, for a caller that cannot await: stderr is read on a task while stdout is read here.</summary>
    public static (string Output, string Error) Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var error = Task.Run(() => ReadAll(process.StandardError.BaseStream));
        var output = ReadAll(process.StandardOutput.BaseStream);
        return (output, error.GetAwaiter().GetResult());
    }

    private static async Task<string> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return Decode(buffer.ToArray());
    }

    private static string ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }
}
