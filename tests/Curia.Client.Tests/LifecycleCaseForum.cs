using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Client;

namespace Curia.Client.Tests;

/// <summary>
/// A Forum double that serves one <c>conformance/lifecycle/</c> case over HTTP: the key set, the log, its head and the
/// entries and proofs the case holds, and nothing else. A reader run against it is run through <see cref="PostVerifier"/>
/// and the routes it fetches, and not handed the documents, so what the Forum lists, withholds and omits is what the
/// reader meets.
///
/// <para>The key set lists the case's supplied lifecycle indices as <c>curia_lifecycle</c> (the post's own leaf
/// excluded) and a key for each binding the case holds, with the binding's leaf as <c>curia_log_index</c> and the status
/// <c>active</c>: the Forum's word, which the lifecycle fold does not take. A key a declaration of the case names is
/// <c>compromised</c> and carries <c>curia_compromised_log_index</c> and <c>curia_disputed_from</c> (R6.57).</para>
/// </summary>
internal sealed class LifecycleCaseForum : HttpMessageHandler
{
    private HttpClient? _http;

    internal static readonly Uri Forum = new("http://forum.test");

    private readonly string _directory;

    internal LifecycleCaseForum(string caseDirectory)
    {
        _directory = caseDirectory;
        using var meta = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(caseDirectory, "meta.json")));
        PostIndex = meta.RootElement.GetProperty("post_index").GetInt64();
        Author = meta.RootElement.GetProperty("agent").GetString()!;
    }

    internal long PostIndex { get; }

    internal string Author { get; }

    /// <summary>Every path asked of this Forum, in order.</summary>
    internal List<string> Requests { get; } = [];

    /// <summary>The lifecycle indices this Forum lists: every entry the case holds but the post's own.</summary>
    internal List<long> Lifecycle() =>
    [
        .. Directory.EnumerateFiles(Path.Combine(_directory, "entries"), "*.json")
            .Select(f => long.Parse(Path.GetFileNameWithoutExtension(f), System.Globalization.CultureInfo.InvariantCulture))
            .Where(i => i != PostIndex)
            .Order(),
    ];

    internal ForumClient Client()
    {
        _http ??= new HttpClient(this, disposeHandler: false) { BaseAddress = Forum };
        return new ForumClient(_http, Forum);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _http?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The post as a read serves it: built from the log's own record of it.</summary>
    internal ProvenancePost Post()
    {
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, "entries", $"{PostIndex}.json")))!;
        var payload = entry["entry"]!["payload"]!;
        string Text(string name) => payload[name]!.GetValue<string>();

        var proof = ActaDocumentsFor(Path.Combine(_directory, "proofs", $"{PostIndex}.json"));
        return new ProvenancePost(
            new Curia.Domain.Serving.Provenance(
                "agent-authored/untrusted", Curia.Domain.Serving.Provenance.StandardWarning, Text("author"),
                OwnerVerified: false, SignatureValid: true, "V0", [], Curia.Domain.Serving.MarkingMode.None, null,
                string.Empty, null, null, [], []),
            Text("post_id"), Text("board"), Text("kind"), null, Text("server_ts"), Text("digest"),
            Text("canonical"), Text("signature"), string.Empty,
            LogIndex: PostIndex,
            InclusionProof: proof);
    }

    private static InclusionProofDocument ActaDocumentsFor(string path)
    {
        var parsed = Curia.Canon.Json.JsonReader.Parse(File.ReadAllBytes(path), Curia.Canon.Json.AdmitLimits.Default);
        return parsed.TryGetValue(out var value, out var error) && ActaDocuments.ReadInclusionProof(value!).TryGetValue(out var proof, out error)
            ? proof!
            : throw new InvalidOperationException($"{path}: {error?.Type}");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(path);

        string? body = path switch
        {
            "/v1/jwks" => await KeySetAsync(cancellationToken).ConfigureAwait(false),
            "/v1/log/head" => await File.ReadAllTextAsync(Path.Combine(_directory, "head.json"), cancellationToken).ConfigureAwait(false),
            "/v1/log/jwks" => await File.ReadAllTextAsync(Path.Combine(_directory, "log-jwks.json"), cancellationToken).ConfigureAwait(false),
            _ when path.StartsWith("/v1/log/entries/", StringComparison.Ordinal) => await ServedAsync("entries", path["/v1/log/entries/".Length..], cancellationToken).ConfigureAwait(false),
            _ when path.StartsWith("/v1/log/proof/", StringComparison.Ordinal) => await ServedAsync("proofs", path["/v1/log/proof/".Length..], cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        var response = body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"type":"curia/stub/not-held","title":"The case does not hold this"}""", Encoding.UTF8, "application/problem+json"),
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return response;
    }

    private async Task<string?> ServedAsync(string kind, string index, CancellationToken ct)
    {
        var file = Path.Combine(_directory, kind, index + ".json");
        return File.Exists(file) ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : null;
    }

    private async Task<string> KeySetAsync(CancellationToken ct)
    {
        var keys = new JsonArray();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_directory, "entries"), "*.json").Order(StringComparer.Ordinal))
        {
            var entry = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false))!;
            if (entry["entry"]!["event_type"]!.GetValue<string>() != "agent.key-bound") continue;
            if (entry["entry"]!["payload"]!["jwk"] is not JsonObject jwk) continue;

            var key = (JsonObject)jwk.DeepClone();
            key["curia_log_index"] = entry["log_index"]!.GetValue<long>();
            key["curia_status"] = "active";
            keys.Add(key);
        }

        // R6.57: a key a declaration names is compromised, and the key set names the declaration (its log index and the index it
        // states the compromise from) for every kid it lists, a cascaded key as well as the first.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_directory, "entries"), "*.json").Order(StringComparer.Ordinal))
        {
            var entry = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false))!;
            if (entry["entry"]!["event_type"]!.GetValue<string>() != "agent.key-compromised") continue;

            var payload = entry["entry"]!["payload"]!;
            foreach (var kid in payload["kids"]!.AsArray().Select(k => k!.GetValue<string>()))
            {
                if (keys.FirstOrDefault(k => k!["kid"]!.GetValue<string>() == kid) is not JsonObject named) continue;

                named["curia_status"] = "compromised";
                named["curia_compromised_log_index"] = entry["log_index"]!.GetValue<long>();
                named["curia_disputed_from"] = payload["from_index"]!.GetValue<long>();
            }
        }

        var lifecycle = new JsonArray();
        foreach (var index in Lifecycle()) lifecycle.Add(index);

        return new JsonObject
        {
            ["keys"] = keys,
            ["curia_lifecycle"] = lifecycle,
            ["curia_lifecycle_kinds"] = new JsonObject { ["approved"] = new JsonArray(), ["enabled"] = new JsonArray() },
        }.ToJsonString();
    }
}
