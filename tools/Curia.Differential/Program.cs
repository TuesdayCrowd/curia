using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

// Curia.Differential is the C# endpoint of Task 7's differential harness
// (docs/superpowers/plans/2026-08-11-canon-testis.md): an NDJSON pipe driving the real
// Curia.Canon implementation -- JsonReader.Parse for ADMIT, JsonReader.ParseUnrestricted
// feeding CanonicalJson.Canonicalize for the pure RFC 8785 path and
// CanonicalJson.CanonicalizeWithNfc for the Curia profile (R6.41) -- so an external
// comparison script can diff its behavior byte-for-byte against curia-testis (Rust) and a
// node oracle without either endpoint knowing the other exists. See conformance/README.md
// for the shared vocabulary (error slugs, profile names) this endpoint must reproduce
// exactly.
//
// The canonicalize/canonicalize_nfc ops used to route through JsonReader.Parse (ADMIT) --
// exactly the conflation errata E2/E4 identified from this run's own findings: `curia-testis`
// keeps its pure canonicalize functions ADMIT-independent, so a C# endpoint gated by ADMIT
// first was comparing two different operations under the same op name, and every one of the
// 1,068 compared lines E2 attributes to this root cause came from exactly this endpoint. Both
// ops now parse via ParseUnrestricted, matching curia-testis's own architecture.
//
// Known blind spot, recorded here because R14.7 (errata E10) requires it to be stated rather
// than rediscovered: every op below takes bytes and parses them before canonicalizing, so this
// protocol can only compare entry points whose input is bytes. CanonicalJson.Canonicalize's
// JsonValue overload -- a tree a caller built rather than parsed -- has no counterpart op and
// cannot be given one, because the inputs that distinguish it are not expressible in the
// alphabet the protocol carries. That is not a hole to plug with a new op; it is a limit on
// what this harness can measure, and the entry point is covered by in-implementation tests
// instead (CanonicalJsonTests.CanonicalizeRejectsARawDuplicateMemberName and its neighbours).
// E10's defect lived there for exactly this reason, and presented as agreement: fed
// {"a":1,"a":2}, both implementations answered curia/admit/duplicate-key, truthfully and
// irrelevantly, because both were answering about their parse paths.
//
// Two defects have now lived in that blind spot, so the enumeration R14.7 asks for is worth
// stating as a list rather than an example. The tree-taking entry point must independently
// reject (a) a raw duplicate member name -- errata E10, found via a silently collapsed event
// payload -- and (b) an unpaired UTF-16 surrogate -- errata E12/E13, found via a silent U+FFFD
// substitution at the UTF-8 encode step. Fed {"a":"\uD800"}, both implementations answer
// curia/admit/unpaired-surrogate under op:"canonicalize", exactly as truthfully and exactly as
// irrelevantly as they answered about duplicates: this protocol cannot make the surrogate reach
// Canonicalize, because JsonReader rejects it while parsing. Both conditions are pinned by
// CanonicalJsonTests instead.
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

// op:"canonicalize" / op:"canonicalize_nfc" -- both parse via ParseUnrestricted (R6.41),
// not the ADMIT-gated Parse: canonicalization must serve any document RFC 8785 defines an
// output for, including one ADMIT's R6.39 caps or R6.33's numeric bound would refuse (the
// RFC author's own rfc8785/input-values.json among them -- see errata E4). A parse failure
// here is therefore a well-definedness rejection (invalid UTF-8, an unpaired surrogate, a
// raw duplicate member name, a non-finite number), never an ADMIT policy rejection, and the
// two diverge on which CanonicalJson entry point runs afterward.
static string Canonicalize(string id, byte[] inputBytes, bool withNfc)
{
    var parsed = JsonReader.ParseUnrestricted(inputBytes);
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
