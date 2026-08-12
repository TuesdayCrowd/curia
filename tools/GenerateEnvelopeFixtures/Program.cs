using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using GenerateEnvelopeFixtures;
using JsonValue = Curia.Canon.Json.JsonValue;

// Task A of docs/superpowers/plans/2026-08-11-canon-testis.md: generate the
// conformance/envelope/ fixture family with the C# implementation (the signer), then
// immediately reload every file from disk and verify it independently -- Step 3 of the
// plan ("prove the fixtures are self-consistent"). Both phases run every time this is
// invoked, so a fixture can never be committed without having been proven to verify (or,
// for the two negative cases, proven to fail for the declared reason).

var repoRoot = FindRepoRoot();
var outputRoot = Path.Combine(repoRoot, "conformance", "envelope");
Directory.CreateDirectory(outputRoot);

var cases = Fixtures.BuildAll();

Console.WriteLine("=== Generating conformance/envelope/ fixtures (signer: Curia.Canon + Curia.Canon.Sodium) ===");
foreach (var c in cases)
{
    WriteCase(outputRoot, c);
    Console.WriteLine($"  wrote {c.Name} ({c.Alg})");
}

Console.WriteLine();
Console.WriteLine("=== Verifying every fixture by re-reading it from disk (independent of generation state) ===");
var failures = 0;
foreach (var c in cases)
{
    if (!VerifyCase(outputRoot, c))
        failures++;
}

Console.WriteLine();
Console.WriteLine("=== Counterfactual sanity check: the two negative cases' signatures genuinely verify ===");
Console.WriteLine("=== against the content/key they actually match, so their failure is the intended one ===");
foreach (var c in cases)
{
    if (!RunCounterfactual(outputRoot, c))
        failures++;
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"RESULT: all {cases.Count} fixtures are self-consistent."
    : $"RESULT: {failures} of {cases.Count} fixtures FAILED self-consistency.");

return failures == 0 ? 0 : 1;

/// <summary>
/// For the two negative cases only: proves the published signature is not simply broken,
/// but verifies against the *matching* content/key -- so its failure against what was
/// actually published is caused specifically by the tamper or the key swap, not by some
/// incidental defect that would make every signature fail. tampered-body's counterfactual
/// re-canonicalizes the in-memory pre-tamper envelope (never written to disk by design --
/// see JsonBuilders.WithField's remarks); wrong-key's counterfactual is derived entirely
/// from committed files (private-keys.json's "actual signer" entry), so it is reproducible
/// by anyone reading the repo later, not only at generation time. Positive cases have
/// nothing to counter-check here and are reported as skipped.
/// </summary>
static bool RunCounterfactual(string outputRoot, FixtureCase c)
{
    var dir = Path.Combine(outputRoot, c.Name);

    if (c.Name == "tampered-body" && c.OriginalEnvelopeForSanityCheck is { } original)
    {
        var doc = ParseSubmission(dir);
        var originalCanonical = Signing.Canonicalize(original);
        using var jwksDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "jwks.json")));
        var header = Signing.ReadHeader(doc.Signature).Match(h => h, e => throw new InvalidOperationException(e.Type));
        var publicKeyBytes = Jwk.ResolvePublicKeyBytes(jwksDoc.RootElement, header.Kid);
        var verify = Signing.Verify(originalCanonical, doc.Signature, header.Alg, header.Kid, publicKeyBytes);
        var ok = verify.IsOk;
        Console.WriteLine($"  [{c.Name}] counterfactual: same signature against the PRE-TAMPER body => " +
                           $"{(ok ? "VERIFIED (confirms the failure above is caused by the tamper)" : "FAILED -- unexpected, investigate")}");
        return ok;
    }

    if (c.Name == "wrong-key")
    {
        var doc = ParseSubmission(dir);
        var canonical = CanonicalJson.CanonicalizeEnvelope(doc.Envelope).Match(b => b, e => throw new InvalidOperationException(e.Type));
        using var privDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "private-keys.json")));
        var actualSignerX = FindJwkFieldByRole(privDoc.RootElement, "actual signer", "x");
        var header = Signing.ReadHeader(doc.Signature).Match(h => h, e => throw new InvalidOperationException(e.Type));
        var actualSignerPublicKey = Jwk.Base64UrlDecode(actualSignerX);
        var verify = Signing.Verify(canonical, doc.Signature, header.Alg, header.Kid, actualSignerPublicKey);
        var ok = verify.IsOk;
        Console.WriteLine($"  [{c.Name}] counterfactual: same signature against the ACTUAL SIGNER's key " +
                           $"(from private-keys.json, not jwks.json) => " +
                           $"{(ok ? "VERIFIED (confirms the failure above is caused by jwks.json publishing the wrong key)" : "FAILED -- unexpected, investigate")}");
        return ok;
    }

    return true; // positive cases: nothing to counter-check
}

static SubmissionDocument ParseSubmission(string dir) =>
    EnvelopeParser.Parse(File.ReadAllBytes(Path.Combine(dir, "submission.json")), AdmitLimits.Default)
        .Match(d => d, e => throw new InvalidOperationException(e.Type));

static string FindJwkFieldByRole(JsonElement keys, string roleContains, string field)
{
    foreach (var entry in keys.GetProperty("keys").EnumerateArray())
    {
        if (entry.TryGetProperty("role", out var role) && role.GetString()!.Contains(roleContains, StringComparison.Ordinal))
            return entry.GetProperty(field).GetString()!;
    }
    throw new InvalidOperationException($"no key with role containing '{roleContains}' found");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Curia.sln not found above " + AppContext.BaseDirectory + ")");
}

static void WriteCase(string outputRoot, FixtureCase c)
{
    var dir = Path.Combine(outputRoot, c.Name);
    Directory.CreateDirectory(dir);

    var submission = JsonBuilders.Obj(
        ("envelope", c.Envelope),
        ("signature", JsonBuilders.Str(c.Signature.Compact)));
    File.WriteAllText(Path.Combine(dir, "submission.json"), JsonBuilders.PrettyPrint(submission) + "\n");

    var canonical = Signing.Canonicalize(c.Envelope);
    var digest = Digests.Sha256(canonical);
    File.WriteAllBytes(Path.Combine(dir, "expected.canonical"), canonical.ToArray());
    File.WriteAllText(Path.Combine(dir, "expected.digest"), digest.ToHex());

    File.WriteAllText(Path.Combine(dir, "jwks.json"), Jwk.ToJsonText(c.Jwks) + "\n");
    File.WriteAllText(Path.Combine(dir, "private-keys.json"), Jwk.ToJsonText(c.PrivateKeys) + "\n");

    File.WriteAllText(Path.Combine(dir, "meta.json"), BuildMeta(c) + "\n");
}

static string BuildMeta(FixtureCase c)
{
    var o = new JsonObject
    {
        ["profile"] = "envelope",
        ["requirement"] = c.Requirement,
        ["alg"] = c.Alg,
        ["note"] = c.Note,
    };
    if (c.ExpectVerifyFailure is not null)
        o["expect-verify-failure"] = c.ExpectVerifyFailure;
    return o.ToJsonString(Jwk.PrettyOptions);
}

static bool VerifyCase(string outputRoot, FixtureCase c)
{
    var dir = Path.Combine(outputRoot, c.Name);

    var submissionBytes = File.ReadAllBytes(Path.Combine(dir, "submission.json"));
    var parsed = EnvelopeParser.Parse(submissionBytes, AdmitLimits.Default);
    if (!parsed.TryGetValue(out var doc, out var parseError))
    {
        Console.WriteLine($"  [{c.Name}] FAIL: submission.json did not parse: {parseError!.Type}");
        return false;
    }

    var canonicalized = CanonicalJson.CanonicalizeEnvelope(doc.Envelope);
    if (!canonicalized.TryGetValue(out var canonical, out var canonError))
    {
        Console.WriteLine($"  [{c.Name}] FAIL: canonicalization failed: {canonError!.Type}");
        return false;
    }

    var expectedCanonical = File.ReadAllBytes(Path.Combine(dir, "expected.canonical"));
    var canonicalMatches = canonical.Span.SequenceEqual(expectedCanonical);

    var digest = Digests.Sha256(canonical);
    var expectedDigest = File.ReadAllText(Path.Combine(dir, "expected.digest"));
    var digestMatches = string.Equals(digest.ToHex(), expectedDigest, StringComparison.Ordinal);

    var headerRead = Signing.ReadHeader(doc.Signature);
    if (!headerRead.TryGetValue(out var header, out var headerError))
    {
        Console.WriteLine($"  [{c.Name}] FAIL: could not read protected header: {headerError!.Type}");
        return false;
    }

    using var jwksDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "jwks.json")));
    byte[] publicKeyBytes;
    try
    {
        publicKeyBytes = Jwk.ResolvePublicKeyBytes(jwksDoc.RootElement, header.Kid);
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"  [{c.Name}] FAIL: could not resolve public key from jwks.json: {ex.Message}");
        return false;
    }

    var verify = Signing.Verify(canonical, doc.Signature, header.Alg, header.Kid, publicKeyBytes);
    var author = FindString(doc.Envelope.Root, "author") ?? "(no author field)";

    var prefix = $"  [{c.Name}] author={author} alg={header.Alg} kid={header.Kid} " +
                 $"digest={digest.ToPrefixed()} canonical={(canonicalMatches ? "match" : "MISMATCH")} " +
                 $"digest-file={(digestMatches ? "match" : "MISMATCH")}";

    bool pass;
    if (c.ExpectVerifyFailure is null)
    {
        var verifyOk = verify.IsOk;
        pass = canonicalMatches && digestMatches && verifyOk;
        var status = verifyOk ? "VERIFIED" : $"VERIFY-FAILED({verify.Match(_ => "", e => e.Type)})";
        Console.WriteLine($"{prefix} verify={status} => {(pass ? "PASS" : "FAIL")}");
    }
    else
    {
        var actualSlug = verify.Match(_ => (string?)null, e => e.Type);
        var failedAsExpected = !verify.IsOk && actualSlug == c.ExpectVerifyFailure;
        pass = canonicalMatches && digestMatches && failedAsExpected;
        Console.WriteLine(
            $"{prefix} expected-failure={c.ExpectVerifyFailure} actual={(actualSlug ?? "(did not fail!)")} " +
            $"=> {(pass ? "PASS" : "FAIL")}");
    }

    return pass;
}

static string? FindString(JsonValue.Object obj, string key) =>
    obj.Members.FirstOrDefault(m => m.Key == key).Value is JsonValue.String s ? s.Value : null;
