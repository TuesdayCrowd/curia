using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

// Curia.Differential is the C# endpoint of Task 7's differential harness
// (docs/superpowers/plans/2026-08-11-canon-testis.md): an NDJSON pipe driving the real
// Curia.Canon implementation -- JsonReader.Parse for ADMIT, CanonicalJson.Canonicalize for
// the pure RFC 8785 path, CanonicalJson.CanonicalizeWithNfc for the Curia profile -- so an
// external comparison script can diff its behavior byte-for-byte against curia-testis
// (Rust) and a node oracle without either endpoint knowing the other exists. See
// conformance/README.md for the shared vocabulary (error slugs, profile names) this
// endpoint must reproduce exactly.
//
// Wire protocol: one JSON object per line in, one JSON object per line out, same order.
// Input:  {"id":"...","op":"admit"|"canonicalize"|"canonicalize_nfc","input_b64":"..."}
// Output: {"id":"...","ok":true,"out_b64":"..."} or {"id":"...","ok":false,"slug":"..."}
// A per-line try/catch converts any exception -- ours or the library's -- into a CRASH
// result rather than letting it end the process: a mid-run crash would desynchronize
// every subsequent line from its request, and the comparison script depends on strict
// line-for-line, in-order correspondence.

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    Console.Out.WriteLine(ProcessLine(line));
}

[SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "The wire protocol requires that no input ever end the process (see the file " +
        "header): a panic anywhere below -- ours or Curia.Canon's -- must become a CRASH finding on " +
        "this one line rather than an unhandled exception that desynchronizes every following line " +
        "from its request. Catching Exception broadly is the specified behavior, not an oversight.")]
static string ProcessLine(string line)
{
    var id = "unknown";
    try
    {
        using var request = JsonDocument.Parse(line);
        var root = request.RootElement;

        // Read "id" first: everything below can fail (a missing "op", invalid base64, a
        // library exception), and once "id" is captured the catch clause below can still
        // report against the right request instead of falling back to the placeholder.
        id = root.GetProperty("id").GetString() ?? "unknown";
        var op = root.GetProperty("op").GetString() ?? "";
        var inputBytes = Convert.FromBase64String(root.GetProperty("input_b64").GetString() ?? "");

        return op switch
        {
            "admit" => Admit(id, inputBytes),
            "canonicalize" => Canonicalize(id, inputBytes, withNfc: false),
            "canonicalize_nfc" => Canonicalize(id, inputBytes, withNfc: true),
            _ => Failure(id, $"csharp/unknown-op: {op}"),
        };
    }
    catch (Exception ex)
    {
        // "Never crash" is a wire-protocol requirement, not an aspiration: catching
        // Exception broadly and reporting it as a CRASH finding is the point of this
        // clause, not an oversight to narrow later.
        return Failure(id, $"csharp/CRASH: {ex.Message}");
    }
}

// op:"admit" -- ADMIT phase only. Success carries no output bytes by contract: out_b64
// MUST be the empty string, because "the input was admitted" is the entire payload.
static string Admit(string id, byte[] inputBytes)
{
    var result = JsonReader.Parse(inputBytes, AdmitLimits.Default);
    return result.Match(
        _ => Success(id, ReadOnlySpan<byte>.Empty),
        error => Failure(id, error.Type));
}

// op:"canonicalize" / op:"canonicalize_nfc" -- both parse with the same ADMIT phase
// first (canonicalization is never reached on input ADMIT itself would reject), then
// diverge on which CanonicalJson entry point runs. A parse failure reports exactly the
// slug ADMIT produced -- never a different, invented one.
static string Canonicalize(string id, byte[] inputBytes, bool withNfc)
{
    var parsed = JsonReader.Parse(inputBytes, AdmitLimits.Default);
    if (!parsed.TryGetValue(out var value, out var parseError))
        return Failure(id, parseError!.Type);

    var canonicalized = withNfc
        ? CanonicalJson.CanonicalizeWithNfc(value)
        : CanonicalJson.Canonicalize(value);

    return canonicalized.Match(
        canonical => Success(id, canonical.Span),
        error => Failure(id, error.Type));
}

static string Success(string id, ReadOnlySpan<byte> outputBytes)
{
    var response = new JsonObject
    {
        ["id"] = id,
        ["ok"] = true,
        ["out_b64"] = Convert.ToBase64String(outputBytes),
    };
    return response.ToJsonString();
}

static string Failure(string id, string slug)
{
    var response = new JsonObject
    {
        ["id"] = id,
        ["ok"] = false,
        ["slug"] = slug,
    };
    return response.ToJsonString();
}
