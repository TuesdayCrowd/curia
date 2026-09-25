using System.Security.Cryptography;
using System.Text;

namespace Curia.Tests.Shared;

/// <summary>
/// An external signer in another process, for R11.20's tests: <c>tests/Shared/test-signer.py</c>
/// behind a one-line wrapper, holding a key this test process generated and then handed over.
///
/// <para><b>Why a real process.</b> <c>ExternalSigner</c>'s whole claim is that the key lives on the
/// other side of a process boundary and only signatures cross it. A fake implementing
/// <c>IAgentSigner</c> in this process would test the interface and none of the boundary: the
/// argument handling, the stdin and stdout encodings, the exit-code contract, and what happens when
/// the other side says no.</para>
///
/// <para><b>This test process generated the key</b>, and so it could sign with it -- which is fine
/// for a test and is the one thing a deployment must not do. What the tests assert is that the
/// <i>agent</i>, and the process acting as it, never hold it: they receive a command path and
/// nothing else.</para>
/// </summary>
internal sealed class TestSigner : IDisposable
{
    private TestSigner(string directory, string command, string kid, byte[] publicKey)
    {
        Directory = directory;
        Command = command;
        Kid = kid;
        PublicKey = publicKey;
    }

    /// <summary>Where the signer keeps its key, its description and its log. Nothing the agent reads.</summary>
    internal string Directory { get; }

    /// <summary>The command a profile records and <c>ExternalSigner</c> runs.</summary>
    internal string Command { get; }

    internal string Kid { get; }

    /// <summary>The signer's public key, SubjectPublicKeyInfo.</summary>
    internal byte[] PublicKey { get; }

    /// <summary>How many signatures the signer process has produced. Read from its own log.</summary>
    internal int Signatures
    {
        get
        {
            var log = Path.Combine(Directory, "signed.log");
            return File.Exists(log) ? File.ReadAllLines(log).Length : 0;
        }
    }

    /// <summary>From now on the signer is up and declines: <c>sign</c> exits 1.</summary>
    internal void Refuse() => File.WriteAllText(Path.Combine(Directory, "refuse"), string.Empty);

    internal static TestSigner Create(string kid)
    {
        // A shell wrapper around a Python script: POSIX only, and said so rather than skipped. A test
        // that returned early here would report R11.20's separation as tested on a platform where
        // it never ran.
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the test signer is a POSIX shell wrapper");

        var directory = System.IO.Directory.CreateTempSubdirectory("curia-test-signer-").FullName;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var d = key.ExportParameters(includePrivateParameters: true).D!;
        var publicKey = key.ExportSubjectPublicKeyInfo();

        File.WriteAllText(Path.Combine(directory, "d"), Convert.ToHexStringLower(d));
        File.WriteAllText(
            Path.Combine(directory, "describe.json"),
            $$"""{"alg":"ES256","kid":"{{kid}}","public_key":"{{Convert.ToBase64String(publicKey)}}"}""");

        var command = Path.Combine(directory, "signer");
        File.WriteAllText(command, $"#!/bin/sh\nexec python3 '{Script()}' '{directory}' \"$@\"\n", Encoding.ASCII);
        File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new TestSigner(directory, command, kid, publicKey);
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

    /// <summary>The script, found from the repository root rather than copied into every output directory.</summary>
    private static string Script()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            var candidate = Path.Combine(at.FullName, "tests", "Shared", "test-signer.py");
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            "tests/Shared/test-signer.py was not found above " + AppContext.BaseDirectory);
    }
}
