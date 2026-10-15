using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodeValue = System.Text.Json.Nodes.JsonValue;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// A Forum that serves what the key verbs read and writes (errata G21): an identity's key set with the
/// lifecycle beside it, a token, and the two lifecycle routes, each answer the test scripts. It checks nothing a
/// real Forum checks; the routes' own behaviour is <c>Curia.Api.Tests</c>'s, and the client library's wire shape
/// is held to the real Forum there. This is the double the verbs' orchestration is run against: what is read,
/// in what order, what is signed, what is sent and what is written.
/// </summary>
internal sealed class KeyActDouble : HttpMessageHandler
{
    internal static readonly Uri Forum = new("http://forum.test");

    /// <summary>A key of the key set a test serves.</summary>
    internal sealed record Key(string Kid, long? LogIndex, string Status = "active", long? RetiredAt = null);

    /// <summary>Every request, in order, as <c>METHOD path</c>.</summary>
    internal List<string> Requests { get; } = [];

    /// <summary>The body of every lifecycle POST, in order.</summary>
    internal List<string> LifecycleBodies { get; } = [];

    /// <summary>How many times the key set was read.</summary>
    internal int KeySetReads { get; private set; }

    /// <summary>The key set served when no sequence entry remains.</summary>
    internal string KeySet { get; set; } = KeySetJson([new Key("a-0000abcd", 1)], [0, 1]);

    /// <summary>Key sets served, one per read, before <see cref="KeySet"/> is.</summary>
    internal Queue<string> KeySetSequence { get; } = [];

    /// <summary>Answers to the lifecycle POSTs, one each, before the default (a 201 for the statement sent).</summary>
    internal Queue<(HttpStatusCode Status, string Body)> LifecycleAnswers { get; } = [];

    /// <summary>The token endpoint's answer.</summary>
    internal (HttpStatusCode Status, string Body) Token { get; set; } = (HttpStatusCode.OK, """{"access_token":"double-token","token_type":"DPoP","expires_in":300}""");

    /// <summary>Answers for other routes, keyed <c>METHOD path</c>.</summary>
    internal Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>The log index a default 201 answers.</summary>
    internal long AcceptedAt { get; set; } = 3;

    internal int LifecyclePosts => Requests.Count(IsLifecyclePost);

    internal static bool IsLifecyclePost(string request) =>
        request.StartsWith("POST /v1/agents/", StringComparison.Ordinal)
        && (request.EndsWith("/keys", StringComparison.Ordinal) || request.EndsWith("/retire", StringComparison.Ordinal));

    /// <summary>A key set document: the keys, the lifecycle indices (null leaves the member out), and the kinds enabled.</summary>
    internal static string KeySetJson(IEnumerable<Key> keys, long[]? lifecycle, string[]? enabled = null)
    {
        var array = new JsonArray();
        foreach (var key in keys)
        {
            var node = new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["alg"] = "ES256",
                ["kid"] = key.Kid,
                ["x"] = Convert.ToBase64String(new byte[32]).Replace('+', '-').Replace('/', '_').TrimEnd('='),
                ["y"] = Convert.ToBase64String(new byte[32]).Replace('+', '-').Replace('/', '_').TrimEnd('='),
                ["curia_not_before"] = "1970-01-01T00:00:00.0000000+00:00",
                ["curia_status"] = key.Status,
            };

            if (key.LogIndex is { } index) node["curia_log_index"] = index;
            if (key.RetiredAt is { } retired) node["curia_retired_log_index"] = retired;
            array.Add(node);
        }

        var document = new JsonObject { ["keys"] = array };
        if (lifecycle is not null) document["curia_lifecycle"] = new JsonArray([.. lifecycle.Select(i => (JsonNode)NodeValue.Create(i)!)]);
        document["curia_lifecycle_kinds"] = new JsonObject
        {
            ["approved"] = new JsonArray(),
            ["enabled"] = new JsonArray([.. (enabled ?? ["rotation", "retirement"]).Select(k => (JsonNode)NodeValue.Create(k)!)]),
        };

        return document.ToJsonString();
    }

    internal static string Problem(string type, string title, string? detail = null) =>
        JsonSerializer.Serialize(detail is null
            ? new Dictionary<string, string> { ["type"] = type, ["title"] = title }
            : new Dictionary<string, string> { ["type"] = type, ["title"] = title, ["detail"] = detail });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var line = $"{request.Method} {path}";
        Requests.Add(line);

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var (status, answer) = Answer(request.Method, path, line, body);

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(answer, Encoding.UTF8, "application/json"),
        };
    }

    private (HttpStatusCode Status, string Body) Answer(HttpMethod method, string path, string line, string body)
    {
        if (Routes.TryGetValue(line, out var scripted)) return scripted;

        if (method == HttpMethod.Post && path == "/oauth/token") return Token;

        if (method == HttpMethod.Get && path == "/v1/jwks")
        {
            KeySetReads++;
            return (HttpStatusCode.OK, KeySetSequence.Count > 0 ? KeySetSequence.Dequeue() : KeySet);
        }

        if (IsLifecyclePost(line))
        {
            LifecycleBodies.Add(body);
            return LifecycleAnswers.Count > 0 ? LifecycleAnswers.Dequeue() : (HttpStatusCode.Created, Accepted(body));
        }

        return (HttpStatusCode.NotFound, Problem("curia/stub/unrouted", "The double serves no " + line));
    }

    private string Accepted(string requestBody)
    {
        using var request = JsonDocument.Parse(requestBody);
        using var statement = JsonDocument.Parse(request.RootElement.GetProperty("statement").GetString()!);
        var root = statement.RootElement;
        var receipt = new Dictionary<string, object>
        {
            ["agent_id"] = root.GetProperty("agent_id").GetString()!,
            ["kid"] = root.GetProperty("kid").GetString()!,
            ["log_index"] = AcceptedAt,
        };

        if (root.TryGetProperty("authorized_by", out var authorizedBy)) receipt["authorized_by"] = authorizedBy.GetString()!;
        return JsonSerializer.Serialize(receipt);
    }
}

/// <summary>
/// A profile on disk, the double it dials, and the client over them: what a key verb's facts run against.
/// </summary>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The rig owns what it creates and disposes it.")]
internal sealed class KeyActRig : IDisposable
{
    internal const string AgentId = "https://agents.example/a";

    private readonly HttpClient _http;

    private KeyActRig(string home, KeyActDouble forum, EnrolledAgent agent, ProfileStore store)
    {
        Home = home;
        Double = forum;
        Agent = agent;
        Store = store;
        _http = new HttpClient(forum, disposeHandler: false) { BaseAddress = KeyActDouble.Forum };
        Client = new ForumClient(_http, KeyActDouble.Forum);
        Session = new ForumSession(Client, agent, store, TimeProvider.System);
    }

    internal string Home { get; }

    internal KeyActDouble Double { get; }

    internal EnrolledAgent Agent { get; private set; }

    internal ProfileStore Store { get; }

    internal ForumClient Client { get; }

    internal ForumSession Session { get; private set; }

    internal string Slug => Agent.Profile.Slug;

    internal string Directory => Store.DirectoryFor(Slug);

    internal static KeyActRig Create(string slug = "a", string kid = "a-0000abcd")
    {
        var home = System.IO.Directory.CreateTempSubdirectory("curia-keyact-").FullName;
        var store = new ProfileStore(home);
        var created = store.Create(slug, AgentId, kid, KeyActDouble.Forum);
        Assert.True(created.TryGetValue(out var agent, out var error), error?.Title);

        var rig = new KeyActRig(home, new KeyActDouble(), agent!, store);
        rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(kid, 1)], [0, 1]);
        return rig;
    }

    /// <summary>The profile as it is on disk now.</summary>
    internal AgentProfile Profile()
    {
        Assert.True(Store.ReadProfile(Slug).TryGetValue(out var profile, out var error), error?.Title);
        return profile!;
    }

    /// <summary>Loads the agent again, as a new run of the client would, and builds a session over it.</summary>
    internal void Reload()
    {
        Agent.Dispose();
        Assert.True(Store.Load(Slug).TryGetValue(out var agent, out var error), error?.Title);
        Agent = agent!;
        Session = new ForumSession(Client, agent!, Store, TimeProvider.System);
    }

    /// <summary>
    /// A fingerprint of what a refused or foreign-stopped verb must leave alone (spec section 4.12): the sha256 of
    /// <c>identity.json</c> and of <c>signing-key.pem</c> (absent for a signer-held key), and the sorted listing of <c>keys/</c>.
    /// </summary>
    internal string Fingerprint()
    {
        var parts = new List<string>();
        foreach (var name in new[] { "identity.json", "signing-key.pem" })
        {
            var path = Path.Combine(Directory, name);
            parts.Add(name + "=" + (File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "absent"));
        }

        var keys = Path.Combine(Directory, "keys");
        parts.Add("keys=" + (System.IO.Directory.Exists(keys)
            ? string.Join(",", System.IO.Directory.EnumerateFileSystemEntries(keys).Select(Path.GetFileName).Order(StringComparer.Ordinal))
            : "absent"));
        return string.Join("|", parts);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the process's standard streams captured. Facts that call it are in the
    /// <c>console</c> collection, which runs apart from the rest.
    /// </summary>
    internal static async Task<(int Exit, string Stdout, string Stderr)> CaptureAsync(Func<Task<int>> action)
    {
        var (savedOut, savedError) = (Console.Out, Console.Error);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await action();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedError);
        }
    }

    /// <summary>A signature checked as a reader checks it: the header's <c>typ</c> and <c>kid</c>, then the signature under <paramref name="spki"/>.</summary>
    internal static void AssertSigned(string statement, string compact, string typ, string kid, ReadOnlyMemory<byte> spki)
    {
        var signature = new JwsSignature(compact);
        Assert.True(DetachedJws.ReadProtectedHeader(signature).TryGetValue(out var header, out var headerError), headerError?.Title);
        Assert.Equal(typ, header!.Typ);
        Assert.Equal(kid, header.Kid);

        Assert.True(JsonReader.Parse(Encoding.UTF8.GetBytes(statement), AdmitLimits.Default).TryGetValue(out var parsed, out _));
        Assert.True(CanonicalJson.CanonicalizeWithNfc(parsed!).TryGetValue(out var canonical, out _));
        Assert.True(
            Encoding.UTF8.GetString(canonical.Span) == statement,
            "the statement sent is not its own canonical form (spec section 4.5)");

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() },
            typ);
        Assert.True(jws.Verify(canonical, signature, new PublicKeyMaterial("ES256", kid, spki)).TryGetValue(out _, out var verifyError), verifyError?.Title);
    }

    /// <summary>The public key (SubjectPublicKeyInfo) of a PEM private key.</summary>
    internal static byte[] PublicOf(string privatePem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return key.ExportSubjectPublicKeyInfo();
    }

    public void Dispose()
    {
        Agent.Dispose();
        _http.Dispose();
        Double.Dispose();
        System.IO.Directory.Delete(Home, recursive: true);
    }
}
