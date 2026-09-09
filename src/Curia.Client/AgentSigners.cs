using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// R11.20's default: the key lives in this process.
///
/// <para><b>This does not satisfy R4.20, and saying otherwise would be the more comfortable
/// error.</b> R4.20's second sentence is a SHALL — "Where a software key is unavoidable, it SHALL
/// be at rest under an OS-provided secret store, never in a repository, environment variable, or
/// configuration file committed anywhere." A <c>0600</c> PEM under <c>~/.curia</c> clears that
/// sentence's <i>floor</i> completely (it is in no repository, no environment variable, no committed
/// configuration) and does not meet its SHALL: a dot-file is not a Keychain, a Secret Service
/// collection, a DPAPI blob or a KMS handle. The MCP adapter plan called this "the bottom of R4.20's
/// custody ladder"; it is below the ladder's only software rung and above its floor, and those are
/// two different facts.</para>
///
/// <para><b>What the deviation actually costs, stated precisely.</b> Not much against a same-host
/// adversary: §3.6 concedes that hardware-backed storage "raises the cost but does not change the
/// conclusion" for a compromised agent host, and Appendix H lists full host compromise as
/// unmitigated residual. What a secret store adds over a <c>0600</c> file is <b>encryption at rest</b>
/// against an offline copy of the filesystem — a stolen disk, a backup, a snapshot — and, on some
/// platforms, an explicit unlock. That single property is the whole of what is given up, and naming
/// it precisely is what separates an honest record from a demand for three platform integrations.</para>
///
/// <para><b>Why it is still the default.</b> §16's decision D2 is open and asks exactly this — "in
/// process, or require a separate signer" — proposing "in-process by default, separated signer
/// supported and required above T1". Shipping the separated signer as the only option would close
/// D2 in code, which is what this project forbids.</para>
/// </summary>
public sealed class InProcessSigner : IAgentSigner, IDisposable
{
    private readonly ECDsa _key;

    public InProcessSigner(ECDsa key, string kid)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);

        _key = key;
        Kid = kid;
    }

    public string Alg => "ES256";

    public string Kid { get; }

    public ReadOnlyMemory<byte> PublicKey => _key.ExportSubjectPublicKeyInfo();

    public byte[] Sign(ReadOnlySpan<byte> signingInput) => _key.SignData(
        signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// R11.20's separation: signing happens in another process, and this one never sees the key.
///
/// <para><b>The protocol below is this project's own and is not normative.</b> R11.20 names "a local
/// signer process or platform keystore" and specifies nothing further — no interface, no wire
/// format, no lifetime, no error contract — and searching both documents for one returns nothing.
/// That absence is deliberate on reflection rather than an oversight to fill: every format this
/// specification freezes has two parties who cannot coordinate, and a signer and its caller are one
/// deployment, chosen together. Publishing a protocol would buy nothing and cost R15.1-shaped
/// permanence. So what follows is a working default, and a conforming signer is anything that keeps
/// the properties, not anything that speaks these two verbs.</para>
///
/// <para><b>The properties that matter</b>, which any replacement must keep: this process cannot
/// obtain the private key through any member of <see cref="IAgentSigner"/>; the signer binds its own
/// <c>alg</c> and <c>kid</c> rather than accepting them from the caller, so a caller cannot ask it
/// to sign as somebody else; and a refusal is distinguishable from a forgery, because
/// <see cref="DetachedJws"/> reports a throw here as <c>curia/jws/signer-refused</c> rather than as
/// an invalid signature.</para>
///
/// <para><b>The protocol.</b> <c>&lt;command&gt; describe</c> writes one line of JSON to stdout —
/// <c>{"alg":…,"kid":…,"public_key":…}</c>, the public key base64 SubjectPublicKeyInfo — and
/// <c>&lt;command&gt; sign</c> reads the signing input as base64url on stdin and writes the
/// signature as base64url on stdout. Both streams are redirected, because a signer that wrote to a
/// terminal would corrupt the MCP transport (stdout is JSON-RPC there).</para>
/// </summary>
public sealed class ExternalSigner : IAgentSigner
{
    private readonly string _command;
    private readonly byte[] _publicKey;

    private ExternalSigner(string command, string alg, string kid, byte[] publicKey)
    {
        _command = command;
        Alg = alg;
        Kid = kid;
        _publicKey = publicKey;
    }

    public string Alg { get; }

    public string Kid { get; }

    public ReadOnlyMemory<byte> PublicKey => _publicKey;

    /// <summary>
    /// Asks the signer what it is. Done once, at construction, so a misconfigured signer fails
    /// where an operator is watching rather than at the first post.
    /// </summary>
    public static Result<ExternalSigner> Describe(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var described = Run(command, "describe", input: null);
        if (!described.TryGetValue(out var output, out var error)) return Result<ExternalSigner>.Fail(error!);

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output!);
            var root = document.RootElement;

            var alg = root.GetProperty("alg").GetString();
            var kid = root.GetProperty("kid").GetString();
            var publicKey = root.GetProperty("public_key").GetString();

            if (string.IsNullOrWhiteSpace(alg) || string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(publicKey))
                return Result<ExternalSigner>.Fail(ClientErrors.SignerUnusable(
                    command, "describe must name a non-empty alg, kid and public_key"));

            return Result<ExternalSigner>.Ok(
                new ExternalSigner(command, alg, kid, Convert.FromBase64String(publicKey)));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Result<ExternalSigner>.Fail(ClientErrors.SignerUnusable(command, ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return Result<ExternalSigner>.Fail(ClientErrors.SignerUnusable(command, ex.Message));
        }
        catch (FormatException ex)
        {
            return Result<ExternalSigner>.Fail(ClientErrors.SignerUnusable(
                command, "public_key is not base64: " + ex.Message));
        }
    }

    /// <summary>
    /// Signs by asking the signer. Throws rather than returning a value, because
    /// <see cref="DetachedJws.Sign(Canon.Canonical.CanonicalBytes, IAgentSigner)"/> is where the
    /// refusal becomes a <c>Result</c> — one place, so the two adapters cannot disagree about what a
    /// failure looks like.
    /// </summary>
    public byte[] Sign(ReadOnlySpan<byte> signingInput)
    {
        var encoded = System.Buffers.Text.Base64Url.EncodeToString(signingInput);
        var signed = Run(_command, "sign", encoded);

        if (!signed.TryGetValue(out var output, out var error))
            throw new CryptographicException($"{error!.Type}: {error.Title}. {error.Detail}");

        try
        {
            return System.Buffers.Text.Base64Url.DecodeFromChars(output!.Trim());
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("the signer's output is not base64url", ex);
        }
    }

    private static Result<string> Run(string command, string verb, string? input)
    {
        var info = new ProcessStartInfo(command)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        info.ArgumentList.Add(verb);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return Result<string>.Fail(ClientErrors.SignerUnusable(command, ex.Message));
        }

        if (process is null) return Result<string>.Fail(ClientErrors.SignerUnusable(command, "the process did not start"));

        using (process)
        {
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                ? Result<string>.Ok(stdout)
                : Result<string>.Fail(ClientErrors.SignerUnusable(
                    command,
                    string.Create(CultureInfo.InvariantCulture, $"{verb} exited {process.ExitCode}: {Compact(stderr)}")));
        }
    }

    private static string Compact(string text) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
