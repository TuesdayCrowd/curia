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
    ImmutableArray<RetainedKey> RetainedKeys = default, ImmutableArray<LifecycleAct> LifecycleActs = default,
    ImmutableArray<PendingKeyAct> PendingKeyActs = default)
{
    /// <summary>The retained keys; empty, never a default array.</summary>
    public ImmutableArray<RetainedKey> Retained => RetainedKeys.IsDefault ? [] : RetainedKeys;

    /// <summary>The acts this profile made; empty, never a default array.</summary>
    public ImmutableArray<LifecycleAct> Acts => LifecycleActs.IsDefault ? [] : LifecycleActs;

    /// <summary>The acts this profile sent and did not hear an answer to (errata G74, R11.99); empty, never a default array.</summary>
    public ImmutableArray<PendingKeyAct> Pending => PendingKeyActs.IsDefault ? [] : PendingKeyActs;
}

/// <summary>
/// A rotation or a retirement this profile sent and does not know the outcome of (errata G74, R11.99): <c>pending_acts</c> in <c>identity.json</c>, absent from the file when empty. The key a rotation generated
/// is the retained key of the same <paramref name="Kid"/>, whose rotation is not in force.
/// </summary>
/// <param name="Kind"><see cref="LifecycleAct.Rotation"/> or <see cref="LifecycleAct.Retirement"/>.</param>
/// <param name="Kid">The key the act bound (a rotation) or ends (a retirement).</param>
/// <param name="SentAt">The time the act was sent, as the client's own clock gave it.</param>
/// <param name="Kept">
/// A pending rotation's kept key as the key set would serve its public members (R11.100): derived when the profile is read from the retained key's file or its external signer, and never written to <c>identity.json</c>;
/// null for a retirement and for a key that could not be read.
/// </param>
public sealed record PendingKeyAct(string Kind, string Kid, DateTimeOffset SentAt, KeptKeyHalf? Kept = null);

/// <summary>
/// The public half of a key as a JWK carries it: <c>kty</c>, <c>crv</c>, <c>x</c> and, for a type that has one, <c>y</c> (errata G74, R11.99 (settling)). What a pending rotation's kept key is compared with in the key set,
/// the key set's own members being the other side of the comparison.
/// </summary>
public sealed record KeptKeyHalf(string Kty, string? Crv, string X, string? Y);

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
/// <param name="RotationInForce">Whether the rotation that bound this key is one the Forum acts on; false for a key a rotation the Forum recorded and does not act on generated (R11.98).</param>
/// <param name="Half">
/// The key's public half as the key set would serve it (errata G75, R11.101): derived when the profile is read from <c>identity.json</c>, from the key's file (under the mode check) or its external signer, and never
/// written to it; null for a key that could not be read, which the <c>SIGNING KEY NOT LIVE</c> line does not name.
/// </param>
public sealed record RetainedKey(
    string Kid, string Alg, string? File = null, string? Signer = null, bool Retired = false, bool RotationInForce = true, KeptKeyHalf? Half = null);

/// <summary>
/// A rotation or a retirement this profile made and the Forum accepted (spec section 4.12):
/// <c>lifecycle_acts</c> in <c>identity.json</c>.
/// </summary>
/// <param name="Kind"><see cref="Rotation"/> or <see cref="Retirement"/>.</param>
/// <param name="Kid">The key the act bound (a rotation) or ended (a retirement).</param>
/// <param name="LogIndex">The log index the Forum appended the act at, as it answered.</param>
/// <param name="InForce">False for an act the Forum recorded and answered it does not act on (R11.98); true for every act a <c>201</c> answered.</param>
public sealed record LifecycleAct(string Kind, string Kid, long LogIndex, bool InForce = true)
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

    /// <summary>The acts <paramref name="profile"/> sent and did not hear an answer to (errata G74, R11.99).</summary>
    public static ImmutableArray<PendingKeyAct> PendingActs(AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Pending;
    }

    /// <summary>
    /// Records an act sent and not heard from (errata G74, R11.99) and returns the profile with it, written to <c>identity.json</c>. An act of the same kind and kid already pending is replaced, so a record is one.
    /// A rotation's key is kept by <see cref="BeginRotation"/>, which writes it first.
    /// </summary>
    public AgentProfile RecordPendingAct(AgentProfile profile, PendingKeyAct act)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(act);

        var recorded = profile with { PendingKeyActs = [.. WithoutPending(profile, act.Kind, act.Kid), act] };
        WritePrivate(Path.Combine(DirectoryFor(profile.Slug), IdentityFile), RenderIdentity(recorded));
        return recorded;
    }

    private static IEnumerable<PendingKeyAct> WithoutPending(AgentProfile profile, string kind, string kid) =>
        profile.Pending.Where(p => !(p.Kind == kind && string.Equals(p.Kid, kid, StringComparison.Ordinal)));

    /// <summary>
    /// Keeps a rotation before it is sent (errata G74, R11.99): the key it generated is written under <c>keys/</c> at 0600 (or recorded as the external signer's command) and then, in one write of <c>identity.json</c>, kept
    /// as a retained key whose rotation is not in force beside the pending act, with the time it was sent and no log index. The key file is on disk before the record names it, so a process that ends between the two
    /// leaves a file no record names and never a record whose key is gone. The key the profile signs with, <c>signing-key.pem</c>, <c>dpop-key.pem</c> and the cached token are not touched.
    /// </summary>
    public AgentProfile BeginRotation(AgentProfile profile, IAgentSigner next, ECDsa? generated, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(next);

        var kept = KeepKey(profile, next, generated);
        var recorded = profile with
        {
            RetainedKeys = [.. profile.Retained.Where(k => !string.Equals(k.Kid, next.Kid, StringComparison.Ordinal)), kept],
            PendingKeyActs = [.. WithoutPending(profile, LifecycleAct.Rotation, next.Kid), new PendingKeyAct(LifecycleAct.Rotation, next.Kid, sentAt, HalfOf(next))],
        };

        WritePrivate(Path.Combine(DirectoryFor(profile.Slug), IdentityFile), RenderIdentity(recorded));
        return recorded;
    }

    /// <summary>Keeps a retirement before it is sent (errata G74, R11.99): the pending act, with the time it was sent. Nothing else is written: the key it ends is already a retained key.</summary>
    public AgentProfile BeginRetirement(AgentProfile profile, string kid, DateTimeOffset sentAt) =>
        RecordPendingAct(profile, new PendingKeyAct(LifecycleAct.Retirement, kid, sentAt));

    /// <summary>The act is sent again after <c>lifecycle-moved</c>: the record's time becomes the retry's, because the window is measured from the last request (R11.99 (settling)).</summary>
    public AgentProfile ResendPending(AgentProfile profile, string kind, string kid, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var held = profile.Pending.FirstOrDefault(p => p.Kind == kind && string.Equals(p.Kid, kid, StringComparison.Ordinal));
        return held is null ? profile : RecordPendingAct(profile, held with { SentAt = sentAt });
    }

    /// <summary>
    /// Ends the record of an act that is not owed (errata G74, R11.99): the Forum answered below 500, the send provably did not leave, or the act was not recorded after the window. One write of <c>identity.json</c> removes
    /// the record and a rotation's retained key, then the key file is deleted, then <c>keys/</c> if nothing is left in it, so a crash between the steps leaves an orphan file and never a record whose key is gone. The
    /// profile as it was before the act began is what is left, down to a directory a first rotation made.
    /// </summary>
    public AgentProfile DropPending(AgentProfile profile, PendingKeyAct act)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(act);

        var (stripped, entry) = Strip(profile, act);
        WritePrivate(Path.Combine(DirectoryFor(profile.Slug), IdentityFile), RenderIdentity(stripped));

        if (entry?.File is { Length: > 0 } file)
        {
            var directory = DirectoryFor(profile.Slug);
            var path = Path.Combine(directory, file);
            if (File.Exists(path)) File.Delete(path);

            var keys = Path.Combine(directory, RetainedKeysDirectory);
            if (Directory.Exists(keys) && !Directory.EnumerateFileSystemEntries(keys).Any()) Directory.Delete(keys);
        }

        return stripped;
    }

    /// <summary>
    /// Ends the record of an act that could be a row the Forum skipped (errata G74, R11.99 (settling), the cell that cannot be told from a stranger's): the record is removed and a rotation's key is kept, as a retained
    /// key whose rotation is not in force with no act recorded (R11.98's state when the Forum names no usable index). Nothing is deleted.
    /// </summary>
    public AgentProfile ReleasePending(AgentProfile profile, PendingKeyAct act)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(act);

        var released = profile with { PendingKeyActs = [.. WithoutPending(profile, act.Kind, act.Kid)] };
        WritePrivate(Path.Combine(DirectoryFor(profile.Slug), IdentityFile), RenderIdentity(released));
        return released;
    }

    /// <summary>The profile without <paramref name="act"/>'s record and, for a rotation, without the retained key it generated, and that key's entry.</summary>
    private static (AgentProfile Profile, RetainedKey? Entry) Strip(AgentProfile profile, PendingKeyAct act)
    {
        var pending = profile with { PendingKeyActs = [.. WithoutPending(profile, act.Kind, act.Kid)] };
        if (act.Kind != LifecycleAct.Rotation) return (pending, null);

        // A key whose rotation the profile has an act for is not this record's to remove.
        var hasAct = profile.Acts.Any(a => a.Kind == LifecycleAct.Rotation && string.Equals(a.Kid, act.Kid, StringComparison.Ordinal));
        var entry = hasAct ? null : profile.Retained.FirstOrDefault(k => string.Equals(k.Kid, act.Kid, StringComparison.Ordinal) && !k.RotationInForce);
        if (entry is null) return (pending, null);

        return (pending with { RetainedKeys = [.. pending.Retained.Where(k => !ReferenceEquals(k, entry))] }, entry);
    }

    /// <summary>
    /// Completes a rotation the Forum recorded that this profile did not hear the answer to (errata G74, R11.99 (settling)): the kept key becomes the one this profile signs with, as a <c>201</c> would have made it, the
    /// act is recorded at <paramref name="logIndex"/> and the record is removed, in one write; and the cached token is deleted. A refusal when the kept key cannot be read, with nothing changed.
    /// </summary>
    public Result<AgentProfile> SettleRotation(AgentProfile profile, PendingKeyAct act, long logIndex)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(act);

        var entry = profile.Retained.FirstOrDefault(k => string.Equals(k.Kid, act.Kid, StringComparison.Ordinal));
        if (entry is null) return Result<AgentProfile>.Fail(ClientErrors.KeyUnreadable($"this profile holds no kept key {act.Kid}"));

        var receipt = new KeyActReceipt(profile.AgentId, act.Kid, logIndex, profile.Kid);
        if (entry.Signer is { Length: > 0 } command)
        {
            if (!ExternalSigner.Describe(command).TryGetValue(out var signer, out var signerError)) return Result<AgentProfile>.Fail(signerError!);

            return string.Equals(signer!.Kid, entry.Kid, StringComparison.Ordinal) && string.Equals(signer.Alg, entry.Alg, StringComparison.Ordinal)
                ? Result<AgentProfile>.Ok(CompleteRotation(profile, signer, null, receipt))
                : Result<AgentProfile>.Fail(ClientErrors.SignerUnusable(command, "it no longer describes the key this profile kept"));
        }

        if (!LoadKey(RetainedKeyFile(profile.Slug, entry.Kid)).TryGetValue(out var key, out var keyError)) return Result<AgentProfile>.Fail(keyError!);

        using var inProcess = new InProcessSigner(key!, entry.Kid);
        return Result<AgentProfile>.Ok(CompleteRotation(profile, inProcess, key, receipt));
    }

    /// <summary>The public half of a signer's key as the key set would serve it, or null when it is not a key this client can render.</summary>
    private static KeptKeyHalf? HalfOf(IAgentSigner signer)
    {
        if (!PublicJwk.Of(new PublicKeyMaterial(signer.Alg, signer.Kid, signer.PublicKey)).TryGetValue(out var jwk, out _)) return null;

        return ClientJson.String(jwk!, "kty") is { } kty && ClientJson.String(jwk!, "x") is { } x
            ? new KeptKeyHalf(kty, ClientJson.String(jwk!, "crv"), x, ClientJson.String(jwk!, "y"))
            : null;
    }

    /// <summary>A retained key's public half, from its file (read under the mode check) or its external signer; null when it cannot be read.</summary>
    private static KeptKeyHalf? HalfOf(string directory, RetainedKey entry)
    {
        if (entry.Signer is { Length: > 0 } command)
            return ExternalSigner.Describe(command).TryGetValue(out var signer, out _) && string.Equals(signer!.Kid, entry.Kid, StringComparison.Ordinal) ? HalfOf(signer) : null;

        if (entry.File is not { Length: > 0 } file || !LoadKey(Path.Combine(directory, file)).TryGetValue(out var key, out _)) return null;

        using var inProcess = new InProcessSigner(key!, entry.Kid);
        return HalfOf(inProcess);
    }

    /// <summary>
    /// Writes the private half of <paramref name="next"/> as a retained key whose rotation is not in force, and returns the entry that names it: <c>keys/&lt;sha256-hex of kid&gt;.pem</c> at 0600, or the external signer's
    /// command when it holds the key.
    /// </summary>
    private RetainedKey KeepKey(AgentProfile profile, IAgentSigner next, ECDsa? generated)
    {
        if ((next as ExternalSigner)?.Command is { Length: > 0 } command)
            return new RetainedKey(next.Kid, next.Alg, Signer: command, RotationInForce: false);

        ArgumentNullException.ThrowIfNull(generated);
        var directory = DirectoryFor(profile.Slug);
        CreatePrivateDirectory(Path.Combine(directory, RetainedKeysDirectory));
        var name = RetainedKeyName(next.Kid);
        WritePrivate(Path.Combine(directory, RetainedKeysDirectory, name), generated.ExportPkcs8PrivateKeyPem());
        return new RetainedKey(next.Kid, next.Alg, RetainedKeysDirectory + "/" + name, RotationInForce: false);
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
    /// has never reached the disk, with one exception: a rotation the Forum answered <c>500</c>
    /// <c>curia/keys/not-honoured</c> says a row exists, and the key generated for it is kept as a retained key whose
    /// rotation is not in force (R11.98), by a method of its own and never by this one.
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

        // A rotation is kept before it is sent (errata G74, R11.99): its record and the key it generated, held as a retained key, end here, in the write that makes the key the one this profile signs with.
        var (stripped, keptEntry) = Strip(profile, new PendingKeyAct(LifecycleAct.Rotation, newSigner.Kid, default));
        var kept = MoveCurrentToRetained(stripped);

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

        // The kept copy of the new key is now signing-key.pem (or the signer's): the file is removed after the record that no longer names it.
        if (keptEntry?.File is { Length: > 0 } keptFile && File.Exists(Path.Combine(directory, keptFile)))
            File.Delete(Path.Combine(directory, keptFile));

        return rotated;
    }

    /// <summary>
    /// Makes the profile sign with a key it holds that the Forum's key set serves live (errata G75, R11.101): one write of <c>identity.json</c> and nothing signed, requested or sent. The key the profile signed with
    /// moves to <c>keys/</c> as a retained key (its rotation is not in force only where the profile holds a rotation act for it at an index the key set's lifecycle does not hold); the chosen key's file becomes
    /// <c>signing-key.pem</c> (or the signer's command is recorded and the file removed); each act whose index the lifecycle does not hold is recorded not in force; the one rotation act whose kid is the chosen key is
    /// recorded in force at the index the key set names for it (and, for a kept key with no act and a binding after the enrolment's, one is made); <c>pending_acts</c> and <c>dpop-key.pem</c> are not touched; the
    /// cached token is deleted. Every key file is in place before the record names it, and the chosen key's old copy under <c>keys/</c> is deleted after the record, so a crash leaves an orphan file and never a record
    /// whose key is gone. A refusal, with nothing written, when the chosen key cannot be read.
    /// </summary>
    public Result<AgentProfile> AdoptKey(AgentProfile profile, string kid, KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(kid);
        ArgumentNullException.ThrowIfNull(keySet);

        var entry = profile.Retained.FirstOrDefault(k => !k.Retired && string.Equals(k.Kid, kid, StringComparison.Ordinal));
        if (entry is null) return Result<AgentProfile>.Fail(ClientErrors.NoRetainedKey(kid));

        var directory = DirectoryFor(profile.Slug);
        string? command = null;
        string? pem = null;
        if (entry.Signer is { Length: > 0 } signerCommand)
        {
            if (!ExternalSigner.Describe(signerCommand).TryGetValue(out var signer, out var signerError)) return Result<AgentProfile>.Fail(signerError!);

            if (!string.Equals(signer!.Kid, entry.Kid, StringComparison.Ordinal) || !string.Equals(signer.Alg, entry.Alg, StringComparison.Ordinal))
                return Result<AgentProfile>.Fail(ClientErrors.SignerUnusable(signerCommand, "it no longer describes the key this profile kept"));

            command = signerCommand;
        }
        else
        {
            var keyFile = RetainedKeyFile(profile.Slug, entry.Kid);
            var loaded = LoadKey(keyFile);
            if (!loaded.TryGetValue(out var key, out var keyError)) return Result<AgentProfile>.Fail(keyError!);

            key!.Dispose();
            pem = File.ReadAllText(keyFile);
        }

        var lifecycle = keySet.Lifecycle is { } held ? held.ToHashSet() : null;
        var acts = profile.Acts.Select(a => lifecycle is not null && !lifecycle.Contains(a.LogIndex) ? a with { InForce = false } : a).ToList();

        var previousRotation = profile.Acts.FirstOrDefault(a => a.Kind == LifecycleAct.Rotation && string.Equals(a.Kid, profile.Kid, StringComparison.Ordinal));
        var previousRotationInForce = previousRotation is null || lifecycle is null || lifecycle.Contains(previousRotation.LogIndex);

        // The chosen key's own rotation act is the Forum's at the index the key set names (R11.101); a key bound at the enrolment's index is the enrolment's and has no rotation to record.
        var served = keySet.Keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal) && string.Equals(k.Status, "active", StringComparison.Ordinal));
        if (served?.LogIndex is { } index)
        {
            var at = acts.FindIndex(a => a.Kind == LifecycleAct.Rotation && string.Equals(a.Kid, kid, StringComparison.Ordinal));
            if (at >= 0)
                acts[at] = acts[at] with { LogIndex = index, InForce = true };
            else if (keySet.Keys.Where(k => k.LogIndex is not null).Select(k => k.LogIndex!.Value).DefaultIfEmpty(index).Min() < index)
                acts.Add(new LifecycleAct(LifecycleAct.Rotation, kid, index));
        }

        // The key this profile signed with is kept first: its copy under keys/ is on disk before the record names it.
        var kept = MoveCurrentToRetained(profile with { RetainedKeys = [.. profile.Retained.Where(k => !string.Equals(k.Kid, entry.Kid, StringComparison.Ordinal) && !string.Equals(k.Kid, profile.Kid, StringComparison.Ordinal))] });
        var previous = kept.Retained[^1] with { RotationInForce = previousRotationInForce };
        var adopted = kept with
        {
            Kid = entry.Kid,
            Alg = entry.Alg,
            Signer = command,
            RetainedKeys = [.. kept.Retained.Take(kept.Retained.Length - 1), previous],
            LifecycleActs = [.. acts],
        };

        if (pem is not null)
            WritePrivate(Path.Combine(directory, SigningKeyFile), pem);
        else if (File.Exists(Path.Combine(directory, SigningKeyFile)))
            File.Delete(Path.Combine(directory, SigningKeyFile));

        WritePrivate(Path.Combine(directory, IdentityFile), RenderIdentity(adopted));
        DeleteToken(profile.Slug);

        // The kept copy of the chosen key is now signing-key.pem (or the signer's): the file is removed after the record that no longer names it.
        if (entry.File is { Length: > 0 } entryFile && File.Exists(Path.Combine(directory, entryFile)))
            File.Delete(Path.Combine(directory, entryFile));

        return Result<AgentProfile>.Ok(adopted);
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
                PendingKeyActs = [.. WithoutPending(profile, LifecycleAct.Retirement, receipt.Kid)],
            },
            new LifecycleAct(LifecycleAct.Retirement, receipt.Kid, receipt.LogIndex));

        WritePrivate(Path.Combine(DirectoryFor(profile.Slug), IdentityFile), RenderIdentity(retired));
        return retired;
    }

    /// <summary>
    /// Records a retirement this profile recorded as not in force that the Forum now acts on (R11.96 (add.), R11.98): the retained key is marked retired and the retirement act the profile holds for it is replaced by the
    /// same act in force, in one write. No second act is recorded: the row is the one the Forum answered <c>500</c> for, at its own log index.
    /// </summary>
    public AgentProfile CompleteReconciledRetirement(AgentProfile profile, string kid)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(kid);

        var reconciled = profile with
        {
            RetainedKeys = [.. profile.Retained.Select(k => string.Equals(k.Kid, kid, StringComparison.Ordinal) ? k with { Retired = true } : k)],
            LifecycleActs = [.. profile.Acts.Select(a => a.Kind == LifecycleAct.Retirement && string.Equals(a.Kid, kid, StringComparison.Ordinal) && !a.InForce ? a with { InForce = true } : a)],
        };

        WritePrivate(Path.Combine(DirectoryFor(profile.Slug), IdentityFile), RenderIdentity(reconciled));
        return reconciled;
    }

    /// <summary>
    /// Keeps a rotation the Forum recorded and answered <c>500</c> <c>curia/keys/not-honoured</c> (R11.98): the key the
    /// rotation generated is kept as a retained key whose rotation is not in force (its private half under
    /// <c>keys/</c> at 0600, or the external signer's command), and the act is recorded as not in force when
    /// <paramref name="logIndex"/> is a whole number of at least 0. The profile does not sign with the new key:
    /// <c>kid</c>, <c>alg</c>, <c>signer</c>, <c>signing-key.pem</c>, <c>dpop-key.pem</c> and the cached token are
    /// not touched, because the Forum does not bind the key.
    /// </summary>
    /// <param name="profile">The profile as it was loaded.</param>
    /// <param name="newSigner">The key the rotation added.</param>
    /// <param name="newPrivateKey">Its private half when this process holds it; null for an external signer, whose command is recorded.</param>
    /// <param name="logIndex">The answer's <c>log_index</c>, or null when it named none the client can use.</param>
    /// <returns>The profile as written.</returns>
    public AgentProfile RecordNotInForceRotation(
        AgentProfile profile, IAgentSigner newSigner, ECDsa? newPrivateKey, long? logIndex)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(newSigner);

        var directory = DirectoryFor(profile.Slug);
        var kept = KeepKey(profile, newSigner, newPrivateKey);

        // The key was kept before the rotation was sent (R11.99): its pending record and its entry are replaced by this one, so the key is held once.
        var recorded = profile with
        {
            RetainedKeys = [.. profile.Retained.Where(k => !string.Equals(k.Kid, newSigner.Kid, StringComparison.Ordinal)), kept],
            PendingKeyActs = [.. WithoutPending(profile, LifecycleAct.Rotation, newSigner.Kid)],
        };
        if (logIndex is >= 0 and var index)
            recorded = RecordAct(recorded, new LifecycleAct(LifecycleAct.Rotation, newSigner.Kid, index, InForce: false));

        WritePrivate(Path.Combine(directory, IdentityFile), RenderIdentity(recorded));
        return recorded;
    }

    /// <summary>
    /// Records a retirement the Forum recorded and answered <c>500</c> <c>curia/keys/not-honoured</c> as made and not in
    /// force (R11.98), when <paramref name="logIndex"/> is a whole number of at least 0. The retained key is not
    /// marked retired: the Forum does not act on the row.
    /// </summary>
    /// <param name="profile">The profile as it was loaded.</param>
    /// <param name="kid">The key the retirement ends.</param>
    /// <param name="logIndex">The answer's <c>log_index</c>, or null when it named none the client can use.</param>
    /// <returns>The profile as written.</returns>
    public AgentProfile RecordNotInForceRetirement(AgentProfile profile, string kid, long? logIndex)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(kid);

        // The retirement was kept before it was sent (R11.99): its pending record ends here, whether or not the answer named an index to record an act at.
        var recorded = profile with { PendingKeyActs = [.. WithoutPending(profile, LifecycleAct.Retirement, kid)] };
        if (logIndex is >= 0 and var index)
            recorded = RecordAct(recorded, new LifecycleAct(LifecycleAct.Retirement, kid, index, InForce: false));
        else if (recorded.Pending.Length == profile.Pending.Length)
            return profile;

        WritePrivate(Path.Combine(DirectoryFor(profile.Slug), IdentityFile), RenderIdentity(recorded));
        return recorded;
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

        if (!profile.Pending.IsEmpty)
            members.Add(new("pending_acts", new JsonValue.Array([.. profile.Pending.Select(RenderPending)])));

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
        if (!key.RotationInForce) members.Add(new("rotation_in_force", new JsonValue.Bool(false)));
        return new JsonValue.Object([.. members]);
    }

    private static JsonValue RenderPending(PendingKeyAct act) =>
        new JsonValue.Object(
        [
            new("kid", new JsonValue.String(act.Kid)),
            new("kind", new JsonValue.String(act.Kind)),
            new("sent_at", new JsonValue.String(act.SentAt.ToString("o", CultureInfo.InvariantCulture))),
        ]);

    private static JsonValue RenderAct(LifecycleAct act)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("kid", new JsonValue.String(act.Kid)),
            new("kind", new JsonValue.String(act.Kind)),
            new("log_index", new JsonValue.Number(act.LogIndex)),
        };

        if (!act.InForce) members.Add(new("in_force", new JsonValue.Bool(false)));
        return new JsonValue.Object([.. members]);
    }

    /// <summary>
    /// A boolean member that is absent (true), true or false; anything else is not a profile this client wrote
    /// (R11.98): <see langword="null"/> names the member as malformed.
    /// </summary>
    private static bool? ReadFlag(JsonValue.Object o, string name) => ClientJson.Member(o, name) switch
    {
        null => true,
        JsonValue.Bool b => b.Value,
        _ => null,
    };

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

            if (ReadFlag(k, "rotation_in_force") is not { } rotationInForce)
                return Result<ImmutableArray<RetainedKey>>.Fail(ClientErrors.MalformedProfile("a retained key's rotation_in_force is a boolean"));

            read.Add(new RetainedKey(kid, alg, file, signer, ClientJson.Member(k, "retired") is JsonValue.Bool { Value: true }, rotationInForce));
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

            if (ReadFlag(a, "in_force") is not { } inForce)
                return Result<ImmutableArray<LifecycleAct>>.Fail(ClientErrors.MalformedProfile("a lifecycle act's in_force is a boolean"));

            read.Add(new LifecycleAct(kind, kid, index, inForce));
        }

        return Result<ImmutableArray<LifecycleAct>>.Ok(read.ToImmutable());
    }

    /// <summary>
    /// <c>pending_acts</c>, read as strictly as the acts are (errata G74, R11.99): absent is none; a member that is not an array, an act whose kind is neither <c>rotation</c> nor <c>retirement</c>, whose kid is empty or whose
    /// <c>sent_at</c> is not an instant is a profile this client did not write. A rotation's kept key is read to give its public half (<see cref="PendingKeyAct.Kept"/>).
    /// </summary>
    private static Result<ImmutableArray<PendingKeyAct>> ReadPending(JsonValue.Object o, ImmutableArray<RetainedKey> retained)
    {
        static Result<ImmutableArray<PendingKeyAct>> Malformed(string cause) =>
            Result<ImmutableArray<PendingKeyAct>>.Fail(ClientErrors.MalformedProfile(cause));

        var member = ClientJson.Member(o, "pending_acts");
        if (member is null) return Result<ImmutableArray<PendingKeyAct>>.Ok([]);
        if (member is not JsonValue.Array items) return Malformed("pending_acts is an array");

        var read = ImmutableArray.CreateBuilder<PendingKeyAct>();
        foreach (var item in items.Items)
        {
            if (item is not JsonValue.Object a
                || ClientJson.String(a, "kind") is not ({ } kind and (LifecycleAct.Rotation or LifecycleAct.Retirement))
                || ClientJson.String(a, "kid") is not { Length: > 0 } kid
                || ClientJson.String(a, "sent_at") is not { } sentAt
                || !DateTimeOffset.TryParse(sentAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                return Malformed("a pending act needs a kind, a kid and the time it was sent");

            var kept = kind == LifecycleAct.Rotation && retained.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal)) is { } entry
                ? entry.Half
                : null;
            read.Add(new PendingKeyAct(kind, kid, at, kept));
        }

        return Result<ImmutableArray<PendingKeyAct>>.Ok(read.ToImmutable());
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

        if (!ReadRetained(o).TryGetValue(out var readRetained, out var retainedError))
            return Result<AgentProfile>.Fail(retainedError!);

        // The public half of each held key is read here, where the profile is read, so that the profile the CLI reads and the one an agent loads both carry it (errata G75, R11.101); it is never rendered.
        var directory = Path.GetDirectoryName(path) ?? ".";
        var retained = readRetained.Select(k => k with { Half = HalfOf(directory, k) }).ToImmutableArray();

        if (!ReadActs(o).TryGetValue(out var acts, out var actsError))
            return Result<AgentProfile>.Fail(actsError!);

        if (!ReadPending(o, retained).TryGetValue(out var pending, out var pendingError))
            return Result<AgentProfile>.Fail(pendingError!);

        return Result<AgentProfile>.Ok(new AgentProfile(
            slug, agentId, kid, alg, forumUri, ClientJson.String(o, "enrolled_at"), ClientJson.String(o, "signer"),
            retained, acts, pending));
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
