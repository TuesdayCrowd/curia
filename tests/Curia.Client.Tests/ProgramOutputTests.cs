using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.64 (errata G17): another program's bytes are decoded as UTF-8, each ill-formed sequence
/// replaced by U+FFFD under the Unicode Standard's maximal-subpart rule, whatever the bytes begin with.
///
/// <para><b>Why the BOM rows.</b> A redirected <c>Process</c>'s reader detects a byte order mark at
/// offset 0 of the stream: on macOS a child whose output began <c>FF FE</c> was read as UTF-16LE, and a
/// leading <c>EF BB BF</c> was dropped, so the child's first bytes chose the decoding rather than the
/// rule (Task 10's review). Detection fires only at offset 0, which is why the signer script writes
/// nothing before the row: a test that put any byte first could not go red.</para>
///
/// <para>Every expected value was worked out by hand from the maximal-subpart rule, not computed by
/// .NET, and is written as an escape: <c>FF</c> and <c>FE</c> are never UTF-8 and are one U+FFFD each;
/// <c>EF BB BF</c> is U+FEFF, kept, since a decoder that applies the rule does not drop it; <c>C3</c>
/// followed by <c>28</c> is a lead byte cut short, one U+FFFD, then <c>(</c>; and <c>F0 9F 98</c> is
/// one maximal subpart of a four-byte sequence, so one U+FFFD.</para>
///
/// <para>The third theory pins both of <c>ProgramOutput</c>'s readers on both streams, because the
/// architecture fact allows the process's own getters inside <c>ProgramOutput</c> and so cannot tell a
/// reader that decodes by the rule from one that detects a mark (Task 10's fix review).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ProgramOutputTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("curia-program-output-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The bytes a program wrote, as hex, and what a reader is shown for them.</summary>
    public static TheoryData<string, string> Rows() => new()
    {
        { "FFFE4100", "\uFFFD\uFFFDA\u0000" },
        { "EFBBBF61", "\uFEFFa" },
        { "C328", "\uFFFD(" },
        { "F09F98", "\uFFFD" },
        { "61", "a" },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void R10_64_AnotherProgramsBytesAreDecodedAsUtf8WithMaximalSubparts(string hex, string expected)
    {
        Assert.Equal(expected, ProgramOutput.Decode(Convert.FromHexString(hex)));
    }

    /// <summary>
    /// The same rule through a real child process: a signer that writes the row to stderr, with nothing
    /// before it, then <c>&gt;</c>, and exits 1. <c>exited 1: </c> is <c>ExternalSigner</c>'s own
    /// message and the <c>&gt;</c> is the script's, so the decoded row is pinned at both ends: the
    /// <c>F0 9F 98</c> row shows exactly one U+FFFD, and nothing else satisfies the assertion.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void R10_64_ASignersStderrIsDecodedAsUtf8WhateverItBeginsWith(string hex, string expected)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the signer is a POSIX script run through python3");

        var script = Path.Combine(_directory, "signer");
        File.WriteAllText(
            script,
            "#!/usr/bin/env python3\n"
            + "import sys\n"
            + $"sys.stderr.buffer.write(bytes.fromhex('{hex}') + b'>')\n"
            + "sys.stderr.buffer.flush()\n"
            + "sys.exit(1)\n",
            Encoding.ASCII);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var described = ExternalSigner.Describe(script);

        Assert.False(described.TryGetValue(out _, out var error), "the signer exited 1, and Describe accepted it");
        Assert.Contains("exited 1: " + expected + ">", error!.Detail, StringComparison.Ordinal);
    }

    /// <summary>Every row of <see cref="Rows"/>, once through each of <c>ProgramOutput</c>'s readers.</summary>
    public static TheoryData<string, string, string> Streams()
    {
        var streams = new TheoryData<string, string, string>();
        foreach (var row in Rows())
        {
            foreach (var reader in new[] { "Read", "ReadAsync" })
                streams.Add(row.Data.Item1, row.Data.Item2, reader);
        }

        return streams;
    }

    /// <summary>
    /// The same rule through both of <c>ProgramOutput</c>'s readers, on both streams: a child that writes
    /// the row to stdout then <c>&gt;</c>, and the row to stderr then <c>&lt;</c>, with nothing before
    /// either, and exits 0. Detection fires only at offset 0, so a reader that detected a mark on either
    /// stream would show the row decoded another way.
    /// </summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task R10_64_BothOfAProcesssStreamsAreDecodedAsUtf8WhateverTheyBeginWith(string hex, string expected, string reader)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the child is a POSIX script run through python3");

        var ct = TestContext.Current.CancellationToken;
        var script = Path.Combine(_directory, "child");
        await File.WriteAllTextAsync(
            script,
            "#!/usr/bin/env python3\n"
            + "import sys\n"
            + $"sys.stdout.buffer.write(bytes.fromhex('{hex}') + b'>')\n"
            + $"sys.stderr.buffer.write(bytes.fromhex('{hex}') + b'<')\n"
            + "sys.stdout.buffer.flush()\n"
            + "sys.stderr.buffer.flush()\n"
            + "sys.exit(0)\n",
            Encoding.ASCII,
            ct);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var process = Process.Start(new ProcessStartInfo(script)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("the child did not start");

        var result = reader == "Read"
            ? ProgramOutput.Read(process)
            : await ProgramOutput.ReadAsync(process, ct);
        await process.WaitForExitAsync(ct);

        Assert.Equal((expected + ">", expected + "<"), result);
    }

    /// <summary>
    /// A signer whose <c>describe</c> output begins with <c>EF BB BF</c> and is otherwise a well-formed
    /// description is refused: the mark is kept as U+FEFF, and no JSON text begins with it. A decoder that
    /// drops the mark would accept this signer; case 89 is its falsification.
    /// </summary>
    [Fact]
    public void R10_64_ASignerWhoseOutputBeginsWithAByteOrderMarkIsRefused()
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the signer is a POSIX script run through python3");

        var script = Path.Combine(_directory, "signer");
        File.WriteAllText(
            script,
            "#!/usr/bin/env python3\n"
            + "import sys\n"
            + "sys.stdout.buffer.write(b'\\xef\\xbb\\xbf{\"alg\":\"ES256\",\"kid\":\"k\",\"public_key\":\"AAAA\"}\\n')\n"
            + "sys.stdout.buffer.flush()\n"
            + "sys.exit(0)\n",
            Encoding.ASCII);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var described = ExternalSigner.Describe(script);

        Assert.False(described.TryGetValue(out _, out var error), "a signer whose output began with a byte order mark was accepted");
        Assert.Equal("curia/client/signer-unusable", error!.Type);
    }
}
