using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Curia.Canon.Json;
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
/// is the one Table 11 input a client can observe for itself -- T1 needs seven days since
/// enrollment -- and because no endpoint reports an agent's tier, so the alternative to storing it
/// is a client that cannot say anything at all about why it was refused.
/// </param>
public sealed record AgentProfile(
    string Slug, string AgentId, string Kid, string Alg, Uri Forum, string? EnrolledAt = null);

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
    {
        Profile = profile;
        SigningKey = signingKey;
        DpopKey = dpopKey;
    }

    public AgentProfile Profile { get; }

    /// <summary>The registered key. Signs post envelopes and client assertions.</summary>
    public ECDsa SigningKey { get; }

    /// <summary>The unregistered key. Signs DPoP proofs and nothing else.</summary>
    public ECDsa DpopKey { get; }

    /// <summary>The registered public key, base64 SubjectPublicKeyInfo, as <c>POST /v1/agents</c> wants it.</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(SigningKey.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        SigningKey.Dispose();
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

    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

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

        var signing = LoadKey(Path.Combine(directory, SigningKeyFile));
        if (!signing.TryGetValue(out var signingKey, out var signingError))
            return Result<EnrolledAgent>.Fail(signingError!);

        var dpop = LoadKey(Path.Combine(directory, DpopKeyFile));
        if (!dpop.TryGetValue(out var dpopKey, out var dpopError))
        {
            signingKey!.Dispose();
            return Result<EnrolledAgent>.Fail(dpopError!);
        }

        return Result<EnrolledAgent>.Ok(new EnrolledAgent(loaded!, signingKey!, dpopKey!));
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

        return ClientJson.Render(members);
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

        return Uri.TryCreate(forum, UriKind.Absolute, out var forumUri)
            ? Result<AgentProfile>.Ok(new AgentProfile(
                slug, agentId, kid, alg, forumUri, ClientJson.String(o, "enrolled_at")))
            : Result<AgentProfile>.Fail(ClientErrors.MalformedProfile("forum is not an absolute URI"));
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
    /// Returns a reason rather than a bool so the refusal can say what to run to fix it.
    /// </summary>
    private static string? ExposedToOthers(string path)
    {
        if (OperatingSystem.IsWindows()) return null;

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode Others =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        return (mode & Others) == 0
            ? null
            : $"{path} is readable beyond its owner ({mode}). A private key readable by anyone " +
              "else authors posts as this agent, permanently and unrevocably. Run: chmod 600 " + path;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, PrivateDirectoryMode);
    }

    /// <summary>
    /// Writes with the private mode applied <i>before</i> any content reaches the file. Creating
    /// the file and chmod-ing it afterwards leaves a window in which the secret exists at the
    /// prevailing umask, which on a shared machine is the whole of the exposure.
    /// </summary>
    private static void WritePrivate(string path, string content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
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
