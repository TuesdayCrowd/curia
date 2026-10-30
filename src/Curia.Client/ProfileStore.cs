using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// A locally enrolled identity: which Forum, which <c>agent_id</c>, which <c>kid</c>.
/// </summary>
/// <param name="Slug">
/// The local nickname the CLI addresses this identity by. Never sent to the Forum -- the Forum
/// knows an agent by its <see cref="AgentId"/>, and conflating the two would make a rename look
/// like a different agent.
/// </param>
/// <param name="EnrolledAt">
/// The <c>enrolled_at</c> the Forum returned, when this profile has enrolled. Recorded because it
/// is the one Table 11 input a client can observe for itself -- T1 needs 48 hours since
/// enrollment -- and, before `GET /v1/budget` reported the tier (errata G49), because nothing else did, so the alternative to storing it
/// is a client that cannot say anything at all about why it was refused.
/// </param>
/// <param name="Signer">
/// The external signer that holds this identity's registered key (R11.20), or
/// <see langword="null"/> when the key is a PEM in this directory.
///
/// <para><b>A property of the identity, fixed at enrolment</b>, rather than a flag each command
/// is given. The registered key is chosen once, when it is registered, and every process that
/// acts as the agent afterwards must use the same one; a custody choice supplied per invocation is
/// one a single forgotten flag reverts -- and the thing it would revert to is a key file this
/// profile does not have, or worse, one it should not be using.</para>
/// </param>
/// <param name="RetainedKeys">
/// The keys this identity signed with before a rotation (errata G21, spec section 4.12), oldest first, each with
/// where its private half is: a file under <c>keys/</c>, or the external signer that holds it. A key stays here
/// after it is retired, marked so, because a post signed under it is still its author's.
/// </param>
/// <param name="LifecycleActs">
/// Every rotation and retirement this profile made and the Forum accepted, with the log index the Forum answered
/// (spec section 4.12). The evidence a client compares the Forum's published lifecycle with: an entry there that
/// is not here was not made by this profile (R4.39).
/// </param>
public sealed record AgentProfile(
    string Slug, string AgentId, string Kid, string Alg, Uri Forum, string? EnrolledAt = null, string? Signer = null,
    ImmutableArray<RetainedKey> RetainedKeys = default, ImmutableArray<LifecycleAct> LifecycleActs = default)
{
    /// <summary>The retained keys; empty, never a default array.</summary>
    public ImmutableArray<RetainedKey> Retained => RetainedKeys.IsDefault ? [] : RetainedKeys;

    /// <summary>The acts this profile made; empty, never a default array.</summary>
    public ImmutableArray<LifecycleAct> Acts => LifecycleActs.IsDefault ? [] : LifecycleActs;
}

/// <summary>
/// A key this identity no longer signs with (spec section 4.12): <c>retained_keys</c> in <c>identity.json</c>.
/// Where its private half is follows from the key itself: <paramref name="Signer"/> is the external signer
/// that holds it, and otherwise it is the file <c>keys/&lt;sha256-hex of kid&gt;.pem</c>, a name derived from
/// the kid and never taken from it.
/// </summary>
/// <param name="Kid">The key's identifier.</param>
/// <param name="Alg">The JWS <c>alg</c> it signs with.</param>
/// <param name="File">The key file, relative to the profile directory; null when an external signer holds it.</param>
/// <param name="Signer">The external signer's command; null when <paramref name="File"/> holds the key.</param>
/// <param name="Retired">Whether the Forum accepted this profile's retirement of the key (R4.39).</param>
public sealed record RetainedKey(string Kid, string Alg, string? File = null, string? Signer = null, bool Retired = false);

/// <summary>
/// A rotation or a retirement this profile made and the Forum accepted (spec section 4.12):
/// <c>lifecycle_acts</c> in <c>identity.json</c>.
/// </summary>
/// <param name="Kind"><see cref="Rotation"/> or <see cref="Retirement"/>.</param>
/// <param name="Kid">The key the act bound (a rotation) or ended (a retirement).</param>
/// <param name="LogIndex">The log index the Forum appended the act at, as it answered.</param>
public sealed record LifecycleAct(string Kind, string Kid, long LogIndex)
{
    /// <summary>The kind of an act that bound a new key.</summary>
    public const string Rotation = "rotation";

    /// <summary>The kind of an act that ended a key.</summary>
    public const string Retirement = "retirement";
}

/// <summary>
/// An enrolled identity with its two private keys loaded.
///
/// <para><b>Two keys, deliberately</b>, following the same split the Forum's own conformance
/// tests use. The <b>signing</b> key is the one the Registrar registered: it answers "which agent
/// is this", and signs both post envelopes and <c>private_key_jwt</c> client assertions. The
/// <b>DPoP</b> key is separate and never registered: it answers "is this the same client that was
/// issued the token". RFC 9449 does not require them to differ; the security argument only
/// becomes visible when they do, and a client that shipped one key for both would teach every
/// reader of its source the wrong lesson.</para>
/// </summary>
public sealed class EnrolledAgent : IDisposable
{
    internal EnrolledAgent(AgentProfile profile, ECDsa signingKey, ECDsa dpopKey)
        : this(profile, new InProcessSigner(signingKey, profile.Kid), dpopKey)
    {
    }

    private EnrolledAgent(AgentProfile profile, IAgentSigner signer, ECDsa dpopKey)
    {
        Profile = profile;
        Signer = signer;
        DpopKey = dpopKey;
    }

    public AgentProfile Profile { get; }

    /// <summary>
    /// The registered key, as a capability rather than as key material (R11.20). Signs post
    /// envelopes <b>and</b> <c>private_key_jwt</c> client assertions — both, which is why delegating
    /// only the first would leave this process able to authenticate as the agent.
    ///
    /// <para>An <see cref="IAgentSigner"/> rather than an <c>ECDsa</c> so that "this process cannot
    /// extract the registered key" is a fact about the type. It was a public <c>ECDsa</c> until this
    /// stage, which made the property unstatable: every holder of an <see cref="EnrolledAgent"/>
    /// could export the private half.</para>
    /// </summary>
    public IAgentSigner Signer { get; }

    /// <summary>
    /// The unregistered key. Signs DPoP proofs and nothing else, and is deliberately <b>not</b>
    /// behind the port.
    ///
    /// <para><b>The split is stated rather than allowed to fall out.</b> R11.20 says "agent private
    /// keys" without distinguishing, and the two are not comparable: theft of the registered key
    /// buys authoring posts as this agent permanently and non-repudiably, while theft of this one is
    /// bounded by a 300-second token and the key is freely rotatable because nobody registered it.
    /// Delegating it would also put a signer round-trip on every HTTP call including the nonce
    /// retry. What that leaves uncovered is worth naming: a flag and an answer-acceptance are
    /// attributed by the DPoP-bound token and carry no signature at all, so this key plus a live
    /// token is enough to raise a flag in the agent's name.</para>
    /// </summary>
    public ECDsa DpopKey { get; }

    /// <summary>The registered public key, base64 SubjectPublicKeyInfo, as <c>POST /v1/agents</c> wants it.</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(Signer.PublicKey.Span);

    /// <summary>
    /// The registered public key as an <see cref="ECDsa"/>, for a caller that must verify something
    /// against it. Caller-owned, and public-only: there is no path from here back to the private
    /// half, which is the point of holding the signer rather than the key.
    /// </summary>
    public ECDsa ExportPublicKey()
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Signer.PublicKey.Span, out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// An identity whose registered key this process does not hold — R11.20's case.
    ///
    /// <para>Public because <c>Curia.Mcp</c> must be able to build one, and because the alternative
    /// it had was <see cref="ProfileStore.Load"/>, which returns an agent holding both private keys.
    /// An <c>InternalsVisibleTo</c> would have made the internal constructor reachable and R11.20
    /// strictly worse: it hands the MCP process the key-bearing path directly.</para>
    /// </summary>
    public static EnrolledAgent WithSigner(AgentProfile profile, IAgentSigner signer, ECDsa dpopKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(dpopKey);

        return new EnrolledAgent(profile, signer, dpopKey);
    }

    public void Dispose()
    {
        if (Signer is IDisposable disposable) disposable.Dispose();
        DpopKey.Dispose();
    }
}

/// <summary>
/// Where an agent's keys and cached token live on disk, and the file modes they live under.
///
/// <para><b>Layout.</b> The root is <c>$CURIA_CLIENT_HOME</c>, defaulting to <c>~/.curia</c>.
/// Deliberately not <c>~/.claude/curia</c>: that path held the local file board this client
/// replaces, and a client that wrote signing keys into a directory whose other contents are a
/// message log invites exactly one bad afternoon. Under the root, one directory per local slug:</para>
///
/// <code>
/// ~/.curia/agents/&lt;slug&gt;/identity.json     0600   agent_id, kid, alg, forum
/// ~/.curia/agents/&lt;slug&gt;/signing-key.pem   0600   the registered key
/// ~/.curia/agents/&lt;slug&gt;/dpop-key.pem      0600   never registered
/// ~/.curia/agents/&lt;slug&gt;/token.json        0600   cached access token, expiry, last DPoP nonce
/// </code>
///
/// <para><b>Modes are checked on read, not merely set on write.</b> Setting <c>0600</c> at
/// creation protects a key this client wrote; it says nothing about one restored from a backup,
/// copied between machines, or created before a umask was fixed. <see cref="Load"/> refuses a
/// private key readable by group or other rather than using it and hoping -- an unauthorised
/// reader of an Ed25519/ES256 private key can author posts as this agent forever, and there is no
/// redaction primitive to take them back.</para>
/// </summary>
public sealed class ProfileStore
{
    private const string IdentityFile = "identity.json";
    private const string SigningKeyFile = "signing-key.pem";
    private const string DpopKeyFile = "dpop-key.pem";
    private const string TokenFile = "token.json";
    private const string RetainedKeysDirectory = "keys";

    public ProfileStore(string root) => Root = root;

    public string Root { get; }

    /// <summary><c>$CURIA_CLIENT_HOME</c>, or <c>~/.curia</c>.</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("CURIA_CLIENT_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".curia");

    public static ProfileStore Default() => new(DefaultRoot);

    public string DirectoryFor(string slug) => Path.Combine(Root, "agents", slug);

    public bool Exists(string slug) => File.Exists(Path.Combine(DirectoryFor(slug), IdentityFile));

    public IEnumerable<string> Slugs()
    {
        var agents = Path.Combine(Root, "agents");
        if (!Directory.Exists(agents)) return [];

        return Directory.EnumerateDirectories(agents)
            .Where(d => File.Exists(Path.Combine(d, IdentityFile)))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Generates both key pairs and writes the profile. Refuses to overwrite an existing slug:
    /// a second <c>enrol</c> onto a live identity would replace the key the Registrar has on
    /// file, and every post signed with the old one would still be out there attributed to an
    /// agent that can no longer produce that signature.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Both keys are handed to the returned EnrolledAgent, which owns and disposes " +
            "them; disposing here would return an agent whose keys cannot sign.")]
    public Result<EnrolledAgent> Create(string slug, string agentId, string kid, Uri forum)
    {
        ArgumentNullException.ThrowIfNull(forum);

        if (Exists(slug)) return Result<EnrolledAgent>.Fail(ClientErrors.ProfileExists(slug));

        // Reject, never repair: a lone UTF-16 surrogate has no UTF-8 encoding, so an identity
        // carrying one has no canonical form to write down and no envelope it could ever sign.
        // Caught here, at the boundary, rather than as a render failure three files later.
        foreach (var (field, value) in new[] { ("slug", slug), ("agent_id", agentId), ("kid", kid) })
            if (ClientJson.HasUnpairedSurrogate(value))
                return Result<EnrolledAgent>.Fail(
                    ClientErrors.MalformedProfile($"{field} contains an unpaired surrogate"));

        var directory = DirectoryFor(slug);
        CreatePrivateDirectory(directory);

        var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        WritePrivate(Path.Combine(directory, SigningKeyFile), signing.ExportPkcs8PrivateKeyPem());
        WritePrivate(Path.Combine(directory, DpopKeyFile), dpop.ExportPkcs8PrivateKeyPem());

        var profile = new AgentProfile(slug, agentId, kid, "ES256", forum);
        WritePrivate(Path.Combine(directory, IdentityFile), RenderIdentity(profile));

        return Result<EnrolledAgent>.Ok(new EnrolledAgent(profile, signing, dpop));
    }

    /// <summary>
    /// Writes a profile whose registered key is held by an external signer (R11.20): the DPoP key
    /// is generated here, and <b>no signing key is</b> -- there is no <c>signing-key.pem</c> in the
    /// directory at all, so nothing in this process could fall back to one.
    ///
    /// <para>The <c>kid</c> and <c>alg</c> are the signer's, not the caller's. The signer binds its
    /// own identity so that a caller cannot ask it to sign as somebody else, and a profile that named
    /// a different <c>kid</c> would enrol a key under a name the signer does not answer to.</para>
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The DPoP key is handed to the returned EnrolledAgent, which owns and disposes it.")]
    public Result<EnrolledAgent> Create(string slug, string agentId, Uri forum, ExternalSigner signer)
    {
        ArgumentNullException.ThrowIfNull(forum);
        ArgumentNullException.ThrowIfNull(signer);

        if (Exists(slug)) return Result<EnrolledAgent>.Fail(ClientErrors.ProfileExists(slug));

        foreach (var (field, value) in new[] { ("slug", slug), ("agent_id", agentId), ("kid", signer.Kid) })
            if (ClientJson.HasUnpairedSurrogate(value))
                return Result<EnrolledAgent>.Fail(
                    ClientErrors.MalformedProfile($"{field} contains an unpaired surrogate"));

        var directory = DirectoryFor(slug);
        CreatePrivateDirectory(directory);

        var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WritePrivate(Path.Combine(directory, DpopKeyFile), dpop.ExportPkcs8PrivateKeyPem());

        var profile = new AgentProfile(slug, agentId, signer.Kid, signer.Alg, forum, Signer: signer.Command);
        WritePrivate(Path.Combine(directory, IdentityFile), RenderIdentity(profile));

        return Result<EnrolledAgent>.Ok(EnrolledAgent.WithSigner(profile, signer, dpop));
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of both keys transfers to the returned EnrolledAgent; the one path " +
            "that abandons a key (the dpop load failing after the signing load succeeded) disposes it " +
            "explicitly below.")]
    public Result<EnrolledAgent> Load(string slug)
    {
        var directory = DirectoryFor(slug);
        var identityPath = Path.Combine(directory, IdentityFile);
        if (!File.Exists(identityPath)) return Result<EnrolledAgent>.Fail(ClientErrors.NoSuchProfile(slug));

        var profile = ReadIdentity(slug, identityPath);
        if (!profile.TryGetValue(out var loaded, out var profileError))
            return Result<EnrolledAgent>.Fail(profileError!);

        // R11.20: an identity whose registered key lives elsewhere is loaded without opening
        // signing-key.pem, whatever is or is not in the directory. Checked before the in-process
        // path rather than as its fallback, because a fallback is exactly the failure mode: a
        // signer that is down would silently hand the key back to this process.
        if (loaded!.Signer is { Length: > 0 } command)
            return LoadDelegated(loaded, directory, command);

        var signing = LoadKey(Path.Combine(directory, SigningKeyFile));
        if (!signing.TryGetValue(out var signingKey, out var signingError))
            return Result<EnrolledAgent>.Fail(signingError!);

        var dpop = LoadKey(Path.Combine(directory, DpopKeyFile));
        if (!dpop.TryGetValue(out var dpopKey, out var dpopError))
        {
            signingKey!.Dispose();
            return Result<EnrolledAgent>.Fail(dpopError!);
        }

        return Result<EnrolledAgent>.Ok(new EnrolledAgent(loaded, signingKey!, dpopKey!));
    }

    /// <summary>
    /// The DPoP key from this directory and the registered key's signer from the profile -- and a
    /// refusal, not a guess, when the signer no longer answers to the identity that was enrolled.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The DPoP key is handed to the returned EnrolledAgent, which owns and disposes it.")]
    private static Result<EnrolledAgent> LoadDelegated(AgentProfile profile, string directory, string command)
    {
        if (!ExternalSigner.Describe(command).TryGetValue(out var signer, out var signerError))
            return Result<EnrolledAgent>.Fail(signerError!);

        // A signer describing another kid or alg holds a different key from the one the Registrar
        // has on file, and every post it signed would be refused by the Forum as unverifiable -- one
        // at a time, each looking like a transient fault. Said once, here, instead.
        if (!string.Equals(signer!.Kid, profile.Kid, StringComparison.Ordinal)
            || !string.Equals(signer.Alg, profile.Alg, StringComparison.Ordinal))
        {
            return Result<EnrolledAgent>.Fail(ClientErrors.SignerUnusable(
                command,
                $"it describes kid={signer.Kid} alg={signer.Alg}, and this profile enrolled kid={profile.Kid} alg={profile.Alg}"));
        }

        var dpop = LoadKey(Path.Combine(directory, DpopKeyFile));
        if (!dpop.TryGetValue(out var dpopKey, out var dpopError))
            return Result<EnrolledAgent>.Fail(dpopError!);

        return Result<EnrolledAgent>.Ok(EnrolledAgent.WithSigner(profile, signer, dpopKey!));
    }

    /// <summary>
    /// The profile alone, without opening a key (spec section 4.9): what <c>curia keys</c> needs, and what a client
    /// needs to re-read the acts another run of it recorded.
    /// </summary>
    public Result<AgentProfile> ReadProfile(string slug)
    {
        var identityPath = Path.Combine(DirectoryFor(slug), IdentityFile);
        return File.Exists(identityPath)
            ? ReadIdentity(slug, identityPath)
            : Result<AgentProfile>.Fail(ClientErrors.NoSuchProfile(slug));
    }

    /// <summary>The cached access token for a slug, or <see langword="null"/> when there is none.</summary>
    public CachedToken? ReadToken(string slug)
    {
        var path = Path.Combine(DirectoryFor(slug), TokenFile);
        if (!File.Exists(path)) return null;

        var parsed = JsonReader.Parse(File.ReadAllBytes(path), ClientJson.Limits);
        if (!parsed.TryGetValue(out var value, out _) || value is not JsonValue.Object o) return null;

        var token = ClientJson.String(o, "access_token");
        var expires = ClientJson.String(o, "expires_at");
        if (token is null || expires is null) return null;

        return DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? new CachedToken(token, at, ClientJson.String(o, "nonce"))
            : null;
    }

    /// <summary>
    /// Forgets the cached access token for a slug (R5.22): the Forum has refused it because the key it was
    /// issued on has ended, so the next request mints one on a live key. Absent is not an error.
    /// </summary>
    public void DeleteToken(string slug)
    {
        var path = Path.Combine(DirectoryFor(slug), TokenFile);
        if (File.Exists(path)) File.Delete(path);
    }

    public void WriteToken(string slug, CachedToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var directory = DirectoryFor(slug);
        CreatePrivateDirectory(directory);

        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("access_token", new JsonValue.String(token.AccessToken)),
            new("expires_at", new JsonValue.String(token.ExpiresAt.ToString("o", CultureInfo.InvariantCulture))),
        };

        if (token.Nonce is { } nonce) members.Add(new("nonce", new JsonValue.String(nonce)));

        WritePrivate(Path.Combine(directory, TokenFile), ClientJson.Render(members));
    }

    /// <summary>
    /// Records what the Forum said at enrollment. Separate from <see cref="Create"/> because the
    /// keys must exist before the enrollment request can be signed, so the answer arrives after
    /// the profile does.
    /// </summary>
    public void RecordEnrollment(AgentProfile profile, string enrolledAt)
    {
        ArgumentNullException.ThrowIfNull(profile);

        WritePrivate(
            Path.Combine(DirectoryFor(profile.Slug), IdentityFile),
            RenderIdentity(profile with { EnrolledAt = enrolledAt }));
    }

    /// <summary>
    /// Where a retired-from key lives: <c>keys/&lt;sha256-hex of kid&gt;.pem</c> under the profile directory (spec
    /// section 4.12). Derived from a hash and never from the kid itself, so a kid holding a path separator or
    /// a dot-dot names nothing outside the directory.
    /// </summary>
    public string RetainedKeyFile(string slug, string kid) =>
        Path.Combine(DirectoryFor(slug), RetainedKeysDirectory, RetainedKeyName(kid));

    private static string RetainedKeyName(string kid) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kid))) + ".pem";

    /// <summary>
    /// Keeps the key this profile signs with as a retained key, ahead of a rotation that replaces it (spec section
    /// 4.12): an in-process key is copied to <c>keys/&lt;sha256-hex of kid&gt;.pem</c> at 0600, an external signer is
    /// recorded by its command. Writes the copy and nothing else, so a rotation the Forum then refuses leaves
    /// nothing but a file no profile names; the profile is returned, not written.
    /// </summary>
    public AgentProfile MoveCurrentToRetained(AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Signer is { Length: > 0 } command)
            return profile with
            {
                RetainedKeys = [.. profile.Retained, new RetainedKey(profile.Kid, profile.Alg, Signer: command)],
            };

        var directory = DirectoryFor(profile.Slug);
        CreatePrivateDirectory(Path.Combine(directory, RetainedKeysDirectory));

        var name = RetainedKeyName(profile.Kid);
        WritePrivate(
            Path.Combine(directory, RetainedKeysDirectory, name),
            File.ReadAllText(Path.Combine(directory, SigningKeyFile)));

        return profile with
        {
            RetainedKeys = [.. profile.Retained, new RetainedKey(profile.Kid, profile.Alg, RetainedKeysDirectory + "/" + name)],
        };
    }

    /// <summary>Adds an act the Forum accepted to the profile's record of them (spec section 4.12); the profile is returned, not written.</summary>
    public static AgentProfile RecordAct(AgentProfile profile, LifecycleAct act)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(act);

        return profile with { LifecycleActs = [.. profile.Acts, act] };
    }

    /// <summary>
    /// Makes a rotation the Forum accepted this profile's own (spec section 4.12): the key it signed with becomes a
    /// retained key, the new key becomes the one it signs with, the act is recorded, the cached token (issued on the
    /// old key) is deleted. Called only after a 201, so a refused or failed rotation leaves
    /// <c>identity.json</c>, <c>signing-key.pem</c> and <c>keys/</c> as they were, and a key generated for it
    /// has never reached the disk.
    /// </summary>
    /// <param name="profile">The profile as it was loaded, signing with the key that authorized the rotation.</param>
    /// <param name="newSigner">The key the rotation added: the signer that signed its possession.</param>
    /// <param name="newPrivateKey">
    /// The new key's private half to write as <c>signing-key.pem</c>, when this process holds it; null when
    /// <paramref name="newSigner"/> is an external signer, whose command is then recorded.
    /// </param>
    /// <param name="receipt">The Forum's answer to the rotation.</param>
    /// <returns>The profile as written.</returns>
    public AgentProfile CompleteRotation(
        AgentProfile profile, IAgentSigner newSigner, ECDsa? newPrivateKey, KeyActReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(newSigner);
        ArgumentNullException.ThrowIfNull(receipt);

        var directory = DirectoryFor(profile.Slug);
        var kept = MoveCurrentToRetained(profile);

        string? command = (newSigner as ExternalSigner)?.Command;
        if (newPrivateKey is not null)
            WritePrivate(Path.Combine(directory, SigningKeyFile), newPrivateKey.ExportPkcs8PrivateKeyPem());
        else if (File.Exists(Path.Combine(directory, SigningKeyFile)))
            File.Delete(Path.Combine(directory, SigningKeyFile));

        var rotated = RecordAct(
            kept with { Kid = newSigner.Kid, Alg = newSigner.Alg, Signer = command },
            new LifecycleAct(LifecycleAct.Rotation, receipt.Kid, receipt.LogIndex));

        WritePrivate(Path.Combine(directory, IdentityFile), RenderIdentity(rotated));
        DeleteToken(profile.Slug);
        return rotated;
    }

    /// <summary>
    /// Makes a retirement the Forum accepted this profile's own (spec section 4.12): the retained key is marked
    /// retired, and its file is kept, because a post it signed is still its author's. The act is recorded.
    /// </summary>
    public AgentProfile CompleteRetirement(AgentProfile profile, KeyActReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(receipt);

        var retired = RecordAct(
            profile with
            {
                RetainedKeys =
                [
                    .. profile.Retained.Select(k => string.Equals(k.Kid, receipt.Kid, StringComparison.Ordinal) ? k with { Retired = true } : k),
                ],
            },
            new LifecycleAct(LifecycleAct.Retirement, receipt.Kid, receipt.LogIndex));

        WritePrivate(Path.Combine(DirectoryFor(profile.Slug), IdentityFile), RenderIdentity(retired));
        return retired;
    }

    /// <summary>
    /// The signer for a retained key that is not yet retired, to sign its retirement (R4.39: only the key it ends
    /// can). A key file is read under the same mode check as the signing key; an external signer must still
    /// describe itself as the key the profile recorded. The caller disposes what comes back when it is
    /// <see cref="IDisposable"/>.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The signer owns the key and is handed to the caller, who disposes it; the one path that " +
            "abandons a key (a signer that is not the retained key) does not create one.")]
    public Result<IAgentSigner> LoadRetained(AgentProfile profile, string kid)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(kid);

        var retained = profile.Retained.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));
        if (retained is null || retained.Retired)
            return Result<IAgentSigner>.Fail(ClientErrors.NoRetainedKey(kid));

        if (retained.Signer is { Length: > 0 } command)
        {
            if (!ExternalSigner.Describe(command).TryGetValue(out var signer, out var signerError))
                return Result<IAgentSigner>.Fail(signerError!);

            return string.Equals(signer!.Kid, retained.Kid, StringComparison.Ordinal)
                && string.Equals(signer.Alg, retained.Alg, StringComparison.Ordinal)
                    ? Result<IAgentSigner>.Ok(signer)
                    : Result<IAgentSigner>.Fail(ClientErrors.SignerUnusable(
                        command, "it no longer describes the key this profile retained"));
        }

        var loaded = LoadKey(RetainedKeyFile(profile.Slug, kid));
        return loaded.TryGetValue(out var key, out var keyError)
            ? Result<IAgentSigner>.Ok(new InProcessSigner(key!, kid))
            : Result<IAgentSigner>.Fail(keyError!);
    }

    private static string RenderIdentity(AgentProfile profile)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("agent_id", new JsonValue.String(profile.AgentId)),
            new("alg", new JsonValue.String(profile.Alg)),
            new("forum", new JsonValue.String(profile.Forum.ToString())),
            new("kid", new JsonValue.String(profile.Kid)),
        };

        if (profile.EnrolledAt is { Length: > 0 } at)
            members.Add(new("enrolled_at", new JsonValue.String(at)));

        if (profile.Signer is { Length: > 0 } signer)
            members.Add(new("signer", new JsonValue.String(signer)));

        if (!profile.Retained.IsEmpty)
            members.Add(new("retained_keys", new JsonValue.Array([.. profile.Retained.Select(RenderRetained)])));

        if (!profile.Acts.IsEmpty)
            members.Add(new("lifecycle_acts", new JsonValue.Array([.. profile.Acts.Select(RenderAct)])));

        return ClientJson.Render(members);
    }

    private static JsonValue RenderRetained(RetainedKey key)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("alg", new JsonValue.String(key.Alg)),
            new("kid", new JsonValue.String(key.Kid)),
        };

        if (key.File is { Length: > 0 } file) members.Add(new("file", new JsonValue.String(file)));
        if (key.Signer is { Length: > 0 } signer) members.Add(new("signer", new JsonValue.String(signer)));
        if (key.Retired) members.Add(new("retired", new JsonValue.Bool(true)));
        return new JsonValue.Object([.. members]);
    }

    private static JsonValue RenderAct(LifecycleAct act) => new JsonValue.Object(
    [
        new("kid", new JsonValue.String(act.Kid)),
        new("kind", new JsonValue.String(act.Kind)),
        new("log_index", new JsonValue.Number(act.LogIndex)),
    ]);

    private static Result<ImmutableArray<RetainedKey>> ReadRetained(JsonValue.Object o)
    {
        var read = ImmutableArray.CreateBuilder<RetainedKey>();
        foreach (var item in ClientJson.Array(o, "retained_keys"))
        {
            if (item is not JsonValue.Object k
                || ClientJson.String(k, "kid") is not { Length: > 0 } kid
                || ClientJson.String(k, "alg") is not { Length: > 0 } alg)
                return Result<ImmutableArray<RetainedKey>>.Fail(ClientErrors.MalformedProfile("a retained key needs a kid and an alg"));

            var file = ClientJson.String(k, "file");
            var signer = ClientJson.String(k, "signer");

            // Held by a file or by a signer, and a file only where the kid derives it: a profile that named a
            // path of its own would have this client open it.
            if ((file is null) == (signer is null)
                || (file is not null && !string.Equals(file, RetainedKeysDirectory + "/" + RetainedKeyName(kid), StringComparison.Ordinal)))
                return Result<ImmutableArray<RetainedKey>>.Fail(ClientErrors.MalformedProfile("a retained key is held by its derived file or by a signer"));

            read.Add(new RetainedKey(kid, alg, file, signer, ClientJson.Member(k, "retired") is JsonValue.Bool { Value: true }));
        }

        return Result<ImmutableArray<RetainedKey>>.Ok(read.ToImmutable());
    }

    private static Result<ImmutableArray<LifecycleAct>> ReadActs(JsonValue.Object o)
    {
        var read = ImmutableArray.CreateBuilder<LifecycleAct>();
        foreach (var item in ClientJson.Array(o, "lifecycle_acts"))
        {
            if (item is not JsonValue.Object a
                || ClientJson.String(a, "kind") is not ({ } kind and (LifecycleAct.Rotation or LifecycleAct.Retirement))
                || ClientJson.String(a, "kid") is not { Length: > 0 } kid
                || ClientJson.WholeNumber(a, "log_index") is not (>= 0 and { } index))
                return Result<ImmutableArray<LifecycleAct>>.Fail(ClientErrors.MalformedProfile("a lifecycle act needs a kind, a kid and a log index"));

            read.Add(new LifecycleAct(kind, kid, index));
        }

        return Result<ImmutableArray<LifecycleAct>>.Ok(read.ToImmutable());
    }

    private static Result<AgentProfile> ReadIdentity(string slug, string path)
    {
        var parsed = JsonReader.Parse(File.ReadAllBytes(path), ClientJson.Limits);
        if (!parsed.TryGetValue(out var value, out var error))
            return Result<AgentProfile>.Fail(ClientErrors.MalformedProfile(error!.Type));

        if (value is not JsonValue.Object o)
            return Result<AgentProfile>.Fail(ClientErrors.MalformedProfile("not a JSON object"));

        var agentId = ClientJson.String(o, "agent_id");
        var kid = ClientJson.String(o, "kid");
        var alg = ClientJson.String(o, "alg");
        var forum = ClientJson.String(o, "forum");

        if (agentId is null || kid is null || alg is null || forum is null)
            return Result<AgentProfile>.Fail(
                ClientErrors.MalformedProfile("agent_id, kid, alg and forum are all required"));

        if (!Uri.TryCreate(forum, UriKind.Absolute, out var forumUri))
            return Result<AgentProfile>.Fail(ClientErrors.MalformedProfile("forum is not an absolute URI"));

        if (!ReadRetained(o).TryGetValue(out var retained, out var retainedError))
            return Result<AgentProfile>.Fail(retainedError!);

        if (!ReadActs(o).TryGetValue(out var acts, out var actsError))
            return Result<AgentProfile>.Fail(actsError!);

        return Result<AgentProfile>.Ok(new AgentProfile(
            slug, agentId, kid, alg, forumUri, ClientJson.String(o, "enrolled_at"), ClientJson.String(o, "signer"),
            retained, acts));
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The key is returned to the caller on success and disposed on every failure " +
            "path below; there is no path that both creates it and drops it.")]
    private static Result<ECDsa> LoadKey(string path)
    {
        if (!File.Exists(path))
            return Result<ECDsa>.Fail(ClientErrors.KeyUnreadable($"{path} does not exist"));

        if (ExposedToOthers(path) is { } exposure)
            return Result<ECDsa>.Fail(ClientErrors.KeyUnreadable(exposure));

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
        }
        catch (CryptographicException ex)
        {
            key.Dispose();
            return Result<ECDsa>.Fail(ClientErrors.KeyUnreadable($"{path}: {ex.Message}"));
        }
        catch (ArgumentException ex)
        {
            key.Dispose();
            return Result<ECDsa>.Fail(ClientErrors.KeyUnreadable($"{path}: {ex.Message}"));
        }

        return Result<ECDsa>.Ok(key);
    }

    /// <summary>
    /// The reason this key file is not safely private, or <see langword="null"/> when it is.
    /// Returns a reason rather than a bool so the refusal can say what to run to fix it -- the
    /// remedy is part of the message because a reader hitting this has no other way to learn it.
    /// </summary>
    private static string? ExposedToOthers(string path) =>
        PrivateFiles.ReadableBeyondOwner(path) is { } exposure
            ? exposure + ". A private key readable by anyone else authors posts as this agent, "
              + "permanently and unrevocably. Run: chmod 600 " + path
            : null;

    private static void CreatePrivateDirectory(string path) => PrivateFiles.CreateDirectory(path);

    /// <summary>
    /// Writes with the private mode applied <i>before</i> any content reaches the file. Creating
    /// the file and chmod-ing it afterwards leaves a window in which the secret exists at the
    /// prevailing umask, which on a shared machine is the whole of the exposure.
    /// </summary>
    private static void WritePrivate(string path, string content) => PrivateFiles.Write(path, content);
}

/// <summary>
/// A cached access token and the nonce the Forum last handed out.
///
/// <para>Access tokens last 300 seconds; caching one avoids a token round-trip per command
/// without ever extending its life. The nonce is cached for the same reason and with the same
/// honesty: RFC 9449 §8's <c>use_dpop_nonce</c> challenge is the normal flow, so a client must
/// handle the challenge whether or not it has a cached value -- the cache saves a round-trip,
/// it does not remove the retry.</para>
/// </summary>
public sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt, string? Nonce)
{
    /// <summary>
    /// Thirty seconds of headroom, because the token has to still be valid when the Forum
    /// evaluates it, not when the client decided to send it.
    /// </summary>
    public bool IsUsableAt(DateTimeOffset now) => ExpiresAt - now > TimeSpan.FromSeconds(30);
}
