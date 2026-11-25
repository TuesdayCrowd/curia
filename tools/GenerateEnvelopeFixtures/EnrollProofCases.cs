using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSec.Cryptography;

namespace GenerateEnvelopeFixtures;

/// <summary>
/// conformance/enroll-proof/ (errata G79, R4.46): one case per verdict the proof of possession of an enrollment request can reach, run by <c>--enroll-proof-cases</c>.
/// A case is a request (the members of <c>POST /v1/agents</c> and its <c>proof</c>) and the context the Forum decides it in: the Forum's clock, the URL it received the
/// request at, the nonces its store reports current, and the <c>jti</c>s already spent.
///
/// <para><b>Each case's <c>expected.json</c> is a literal in this file, written from the case's construction and the step of R4.46 it targets, and never produced by
/// running a validator.</b> This tool does not reference <c>Curia.AuthN</c>, so there is none to run; the family is the specification a second implementation is built
/// from. Each case breaks one thing, so the refusal cannot come from another cause; the cases that break two say which step decides and are named for the order
/// (<c>*-before-*</c>).</para>
///
/// <para><b>Anchors from outside this repository.</b> <c>ed25519-valid</c> signs with the RFC 8037 Appendix A key, and its <c>jkt</c> is the thumbprint printed in that
/// RFC (<see cref="Rfc8037Thumbprint"/>, asserted below to equal the one this tool computes). <c>es256-valid</c> signs with the RFC 7515 Appendix A.3 key, whose
/// thumbprint <see cref="Rfc7515Thumbprint"/> was computed independently, in Python, by the method recorded in each case's <c>meta.json</c>
/// (<c>jkt-method</c>); the tool asserts it equals its own. Every other Ed25519 key is a fixed seed (0x41, 0x42 and 0x43 repeated), so every Ed25519 byte is reproducible.</para>
///
/// <para><b>ES256 is randomized.</b> A signature is kept from the existing output when the signing input is the same and it verifies under the key; the tool signs only what
/// no existing case pins. A regeneration over the existing output is byte-identical, and one from a clean directory is not: the signatures would differ.</para>
/// </summary>
internal static class EnrollProofCases
{
    private const string Requirement = "R4.46";
    private const string Typ = "curia-enroll-pop+jwt";
    private const string Audience = "https://forum.example/v1/agents";
    private const long Now = 1_800_000_000;
    private const string AgentId = "agent://curia.example/tuesdaycrowd/enrollee";
    private const string OtherAgentId = "agent://curia.example/tuesdaycrowd/someone-else";
    private const string Kid = "enrollee-ed25519";
    private const string EsKid = "enrollee-es256";
    private const string Nonce = "N1-the-forums-current-nonce";
    private const string Nonce2 = "N2-the-forums-other-current-nonce";
    private const int ProofCap = 8192;

    // RFC 8037 Appendix A.1 (the private key's d) and A.2 (its public x), and A.3's thumbprint of it.
    private const string Rfc8037D = "nWGxne_9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A";
    private const string Rfc8037X = "11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo";
    private const string Rfc8037Thumbprint = "kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k";

    // RFC 7515 Appendix A.3's P-256 key. The thumbprint is computed in Python:
    //   python3 -c "import hashlib,base64,json; print(base64.urlsafe_b64encode(hashlib.sha256(json.dumps({'crv':'P-256','kty':'EC','x':X,'y':Y},sort_keys=True,separators=(',',':')).encode()).digest()).rstrip(b'=').decode())"
    private const string Rfc7515X = "f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU";
    private const string Rfc7515Y = "x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0";
    private const string Rfc7515D = "jpsQnnGQmL-YBIffH1136cspYG6-0iY7X1fCE9-E9LI";
    private const string Rfc7515Thumbprint = "oKIywvGUpTVTyxMQ3bwIIeQUudfr_CkLMjCE19ECD-U";

    private const string JktMethod =
        "RFC 7638: SHA-256 over {\"crv\",\"kty\",\"x\"[,\"y\"]} sorted, no whitespace, base64url unpadded; computed in Python (hashlib, base64, json.dumps sort_keys separators (',', ':')), not by this repository's code";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Signer(string Alg, string Kid, byte[] StoredPublic, string Jkt, Func<byte[], byte[]> Sign, Func<byte[], byte[], bool> Verify);

    private sealed record Context(string Audience, string[] CurrentNonces, string[] SpentJtis, long Now);

    /// <summary><paramref name="Detail"/> is the member or claim the refusal names, when the construction fixes one.</summary>
    private sealed record Expect(string Verdict, string? Type, string? Detail)
    {
        public static Expect Ok { get; } = new("ok", null, null);

        public static Expect Refused(string slug, string? detail = null) => new("refused", "curia/enroll/" + slug, detail);
    }

    private sealed record Case(string Name, string Step, string Note, Signer RequestKey, string? Proof, Expect Expected, Context Context, string AgentIdOnRequest, string KidOnRequest, bool Anchors = false);

    private static Dictionary<string, byte[]> _esPins = new(StringComparer.Ordinal);

    public static int Run(string repoRoot)
    {
        var outputRoot = Path.Combine(repoRoot, "conformance", "enroll-proof");
        _esPins = ReadEsPins(outputRoot);

        var rfcEd = Ed("epv-rfc8037", FromB64Url(Rfc8037D), Rfc8037Thumbprint);
        if (!string.Equals(Jwk.Base64UrlEncode(rfcEd.StoredPublic), Rfc8037X, StringComparison.Ordinal))
            throw new InvalidOperationException("the RFC 8037 seed does not derive the RFC's public key");
        var wrongEd = Ed("epv-wrong", Seed(0x41));
        var otherEd = Ed("epv-other", Seed(0x42));
        var rfcEs = Es(FromB64Url(Rfc7515D), Rfc7515Thumbprint);
        if (!string.Equals(Jwk.Base64UrlEncode(Rfc7515PublicX()), Rfc7515X, StringComparison.Ordinal))
            throw new InvalidOperationException("the RFC 7515 scalar does not derive the RFC's public key");

        var ctx = new Context(Audience, [Nonce], [], Now);
        var cases = new List<Case>();

        // A valid proof, parametrised by what one case breaks. The signer is the request's key unless a case says otherwise.
        JsonObject Payload(Signer key, string? agentId = null, string? jkt = null, string? aud = null, long? iat = null, string? jti = null, string? nonce = null) => new()
        {
            ["agent_id"] = agentId ?? AgentId,
            ["aud"] = aud ?? Audience,
            ["iat"] = iat ?? Now,
            ["jkt"] = jkt ?? key.Jkt,
            ["jti"] = jti ?? "jti-default",
            ["nonce"] = nonce ?? Nonce,
        };

        string Valid(Signer key, string jti, Action<JsonObject>? payload = null, Action<JsonObject>? header = null, Signer? signedBy = null, string? kid = null)
        {
            var h = Header(key.Alg, kid ?? key.Kid);
            header?.Invoke(h);
            var p = Payload(key, jti: jti);
            payload?.Invoke(p);
            return Compose(h, p, signedBy ?? key);
        }

        void Add(string name, string step, string note, Signer key, string? proof, Expect expected, Context? context = null, string? agentId = null, string? kid = null, bool anchors = false) =>
            cases.Add(new Case(name, step, note, key, proof, expected, context ?? ctx, agentId ?? AgentId, kid ?? key.Kid, anchors));

        // ---- accepted ----------------------------------------------------------------------------------------------------------------------------------------
        Add("ed25519-valid", "all", "The RFC 8037 Appendix A key signs a proof that names every claim as the request has it; the jkt is the thumbprint that RFC prints.", rfcEd,
            Valid(rfcEd, "jti-ed25519-valid"), Expect.Ok, anchors: true);
        Add("es256-valid", "all", "The RFC 7515 Appendix A.3 key signs the same shape under ES256 (raw R||S, 64 bytes); the signature is pinned.", rfcEs,
            Valid(rfcEs, "jti-es256-valid"), Expect.Ok, anchors: true);
        Add("unknown-claims-ignored", "5", "Two claims no step names (code, extra) are in the payload; they are ignored (R4.46: the enrollment code is not built, so a claim named code is read by nothing).", rfcEd,
            Valid(rfcEd, "jti-unknown-claims", p => { p["code"] = "not-built"; p["extra"] = 1; }), Expect.Ok);
        Add("unknown-header-members-ignored", "2", "The protected header carries one member no step names (cty); it is ignored.", rfcEd,
            Valid(rfcEd, "jti-unknown-header", header: h => h["cty"] = "x"), Expect.Ok);
        Add("second-of-two-current-nonces", "6", "The store reports two nonces current (this epoch's and the previous one's); the proof carries the second.", rfcEd,
            Valid(rfcEd, "jti-two-nonces", p => p["nonce"] = Nonce2), Expect.Ok, ctx with { CurrentNonces = [Nonce, Nonce2] });
        Add("iat-30s-old", "5", "iat is exactly 30 seconds before the Forum's clock: inside the skew (accepting side of the boundary).", rfcEd,
            Valid(rfcEd, "jti-iat-30-old", p => p["iat"] = Now - 30), Expect.Ok);
        Add("iat-30s-future", "5", "iat is exactly 30 seconds after the Forum's clock: inside the skew.", rfcEd,
            Valid(rfcEd, "jti-iat-30-future", p => p["iat"] = Now + 30), Expect.Ok);
        Add("proof-at-the-cap", "2", $"A valid proof of exactly {ProofCap} characters (a padding claim fills it): not over the cap.", rfcEd,
            SizedProof(rfcEd, "jti-at-cap", ProofCap), Expect.Ok);

        // ---- step 1: required -----------------------------------------------------------------------------------------------------------------------------------
        Add("proof-absent", "1", "The request has no proof member.", rfcEd, null, Expect.Refused("proof-required", "member=proof"));
        Add("proof-empty", "1", "The request's proof is the empty string.", rfcEd, "", Expect.Refused("proof-required", "member=proof"));

        // ---- step 2: malformed (by what the proof is) ---------------------------------------------------------------------------------------------------------
        Add("proof-over-the-cap", "2", $"A valid proof of {ProofCap + 1} characters: one over the cap, refused before anything is decoded.", rfcEd,
            SizedProof(rfcEd, "jti-over-cap", ProofCap + 1), Expect.Refused("proof-malformed", "member=proof"));
        var good = Valid(rfcEd, "jti-segments");
        var segs = good.Split('.');
        Add("two-segments", "2", "Header and payload with no signature segment and no second full stop.", rfcEd, segs[0] + "." + segs[1], Expect.Refused("proof-malformed", "member=proof"));
        Add("four-segments", "2", "A valid proof with a fourth segment appended.", rfcEd, good + ".AAAA", Expect.Refused("proof-malformed", "member=proof"));
        Add("padded-base64url", "2", "The header segment is spelled with base64 padding (an equals sign) though the bytes it names are a valid header; the signature is over the unpadded form.", rfcEd,
            PaddedHeader(rfcEd, "jti-padded"), Expect.Refused("proof-malformed", "member=proof"));
        Add("standard-base64-alphabet", "2", "The signature segment uses the standard alphabet (a plus sign) in place of base64url's hyphen, the same bytes otherwise.", rfcEd,
            StandardAlphabetSignature(rfcEd, "jti-std-alphabet"), Expect.Refused("proof-malformed", "member=proof"));
        Add("header-not-an-object", "2", "The header segment decodes to a JSON array.", rfcEd,
            Raw("[\"EdDSA\"]", Payload(rfcEd, jti: "jti-header-array").ToJsonString(Compact), rfcEd), Expect.Refused("proof-malformed", "member=header"));
        Add("header-not-json", "2", "The header segment decodes to text that is not JSON.", rfcEd,
            Raw("not json at all", Payload(rfcEd, jti: "jti-header-text").ToJsonString(Compact), rfcEd), Expect.Refused("proof-malformed", "member=header"));
        Add("typ-dpop", "2", "The header's typ is dpop+jwt (a DPoP proof's): a proof for another purpose is not an enrollment proof.", rfcEd,
            Valid(rfcEd, "jti-typ-dpop", header: h => h["typ"] = "dpop+jwt"), Expect.Refused("proof-malformed", "member=typ"));
        Add("typ-client-assertion", "2", "The header's typ is JWT (a client assertion's).", rfcEd,
            Valid(rfcEd, "jti-typ-jwt", header: h => h["typ"] = "JWT"), Expect.Refused("proof-malformed", "member=typ"));
        Add("typ-absent", "2", "The header has no typ.", rfcEd,
            Valid(rfcEd, "jti-typ-absent", header: h => h.Remove("typ")), Expect.Refused("proof-malformed", "member=typ"));
        Add("alg-none", "2", "The header's alg is none (with a signature segment of the right length, so the alg is the only fault).", rfcEd,
            Raw(Header("none", Kid).ToJsonString(Compact), Payload(rfcEd, jti: "jti-alg-none").ToJsonString(Compact), rfcEd), Expect.Refused("proof-malformed", "member=alg"));
        Add("alg-hs256", "2", "The header's alg is HS256 (outside the allow-list), with a 32-byte MAC-sized signature segment.", rfcEd,
            RawWithSignature(Header("HS256", Kid).ToJsonString(Compact), Payload(rfcEd, jti: "jti-alg-hs256").ToJsonString(Compact), new byte[32]), Expect.Refused("proof-malformed", "member=alg"));
        Add("kid-absent-from-header", "2", "The header has no kid.", rfcEd,
            Valid(rfcEd, "jti-kid-absent", header: h => h.Remove("kid")), Expect.Refused("proof-malformed", "member=kid"));

        // ---- step 3: header mismatch --------------------------------------------------------------------------------------------------------------------------------
        Add("alg-differs-from-request", "3", "The request says EdDSA; the proof is a well-formed ES256 proof signed by an ES256 key: refused at the header, before any signature is checked.", rfcEd,
            Valid(rfcEs, "jti-alg-differs", signedBy: rfcEs, kid: Kid), Expect.Refused("proof-mismatch", "claim=alg"));
        Add("kid-differs", "3", "The header's kid is not the request's, and the signature verifies under the request's key.", rfcEd,
            Valid(rfcEd, "jti-kid-differs", kid: "another-kid"), Expect.Refused("proof-mismatch", "claim=kid"));
        Add("kid-differs-before-wrong-key", "3", "The header's kid is not the request's, and the signature is by a stranger: the header is decided first.", rfcEd,
            Valid(rfcEd, "jti-kid-then-key", signedBy: wrongEd, kid: "another-kid"), Expect.Refused("proof-mismatch", "claim=kid"));

        // ---- step 4: the signature -----------------------------------------------------------------------------------------------------------------------------------
        Add("wrong-key", "4", "A proof valid in every claim, signed by a key other than the request's.", rfcEd,
            Valid(rfcEd, "jti-wrong-key", signedBy: wrongEd), Expect.Refused("proof-not-possessed"));
        Add("signature-bit-flipped", "4", "A valid proof whose signature has one bit changed.", rfcEd,
            FlipSignatureBit(Valid(rfcEd, "jti-bit-flipped")), Expect.Refused("proof-not-possessed"));
        Add("signature-truncated", "4", "A valid proof whose signature segment is 63 bytes (base64url, well formed).", rfcEd,
            TruncateSignature(rfcEd, "jti-truncated"), Expect.Refused("proof-not-possessed"));
        Add("proof-for-another-body", "4", "A proof made by key A for key A's identity and thumbprint, sent with key B as the request's public_key: the signature does not verify under the request's key, and is decided before its claims are read.", otherEd,
            Valid(rfcEd, "jti-another-body"), Expect.Refused("proof-not-possessed"), kid: Kid);
        Add("header-carries-its-own-key", "4", "A proof signed by key B whose protected header carries a jwk member holding B's public key (an unknown header member, ignored by R4.46 step 2); the request submits key A, and every claim is A's. The verifier is chosen by the request's alg and the key by the request's public_key, never by the proof (step 4), so the signature does not verify and the proof is not possessed.", rfcEd,
            Valid(rfcEd, "jti-header-own-key", header: h => h["jwk"] = new JsonObject { ["crv"] = "Ed25519", ["kty"] = "OKP", ["x"] = Jwk.Base64UrlEncode(wrongEd.StoredPublic) }, signedBy: wrongEd), Expect.Refused("proof-not-possessed"));
        Add("wrong-key-and-wrong-claims", "4", "Signed by a stranger and naming another identity: the signature is decided before the payload is read.", rfcEd,
            Valid(rfcEd, "jti-wrong-key-claims", p => p["agent_id"] = OtherAgentId, signedBy: wrongEd), Expect.Refused("proof-not-possessed"));

        // ---- step 5: payload ------------------------------------------------------------------------------------------------------------------------------------------
        foreach (var claim in new[] { "agent_id", "jkt", "aud", "iat", "jti" })
        {
            Add($"payload-missing-{claim.Replace('_', '-')}", "5", $"A signed payload that has no {claim} claim.", rfcEd,
                Valid(rfcEd, "jti-missing-" + claim, p => p.Remove(claim)), Expect.Refused("proof-malformed", "member=" + claim));
        }

        Add("payload-iat-not-a-number", "5", "A signed payload whose iat is the string \"1800000000\".", rfcEd,
            Valid(rfcEd, "jti-iat-string", p => p["iat"] = "1800000000"), Expect.Refused("proof-malformed", "member=iat"));
        Add("payload-not-an-object", "5", "A signed payload that is a JSON array.", rfcEd,
            Raw(Header("EdDSA", Kid).ToJsonString(Compact), "[1]", rfcEd), Expect.Refused("proof-malformed", "member=payload"));
        Add("payload-malformed-before-mismatch", "5", "agent_id is another's and jti is absent: a claim that does not read is decided before a claim that does not match.", rfcEd,
            Valid(rfcEd, "jti-x", p => { p["agent_id"] = OtherAgentId; p.Remove("jti"); }), Expect.Refused("proof-malformed", "member=jti"));
        Add("wrong-identity", "5", "agent_id names another identity; the signature is valid.", rfcEd,
            Valid(rfcEd, "jti-wrong-identity", p => p["agent_id"] = OtherAgentId), Expect.Refused("proof-mismatch", "claim=agent_id"));
        Add("wrong-thumbprint", "5", "jkt is another key's thumbprint; the signature is valid.", rfcEd,
            Valid(rfcEd, "jti-wrong-jkt", p => p["jkt"] = wrongEd.Jkt), Expect.Refused("proof-mismatch", "claim=jkt"));
        Add("wrong-audience-host", "5", "aud is another host's enrollment URL.", rfcEd,
            Valid(rfcEd, "jti-aud-host", p => p["aud"] = "https://elsewhere.example/v1/agents"), Expect.Refused("proof-mismatch", "claim=aud"));
        Add("wrong-audience-trailing-slash", "5", "aud is the Forum's URL with a trailing slash on the path.", rfcEd,
            Valid(rfcEd, "jti-aud-slash", p => p["aud"] = Audience + "/"), Expect.Refused("proof-mismatch", "claim=aud"));
        Add("wrong-audience-scheme", "5", "aud is the Forum's URL under http for https.", rfcEd,
            Valid(rfcEd, "jti-aud-scheme", p => p["aud"] = "http://forum.example/v1/agents"), Expect.Refused("proof-mismatch", "claim=aud"));
        Add("agent-id-before-jkt", "5", "agent_id and jkt both name another: agent_id is decided first.", rfcEd,
            Valid(rfcEd, "jti-order-1", p => { p["agent_id"] = OtherAgentId; p["jkt"] = wrongEd.Jkt; }), Expect.Refused("proof-mismatch", "claim=agent_id"));
        Add("jkt-before-aud", "5", "jkt and aud both name another: jkt is decided first.", rfcEd,
            Valid(rfcEd, "jti-order-2", p => { p["jkt"] = wrongEd.Jkt; p["aud"] = "https://elsewhere.example/v1/agents"; }), Expect.Refused("proof-mismatch", "claim=jkt"));
        Add("mismatch-before-window", "5", "aud is another host's and iat is 31 seconds old: a claim that does not match is decided before the window.", rfcEd,
            Valid(rfcEd, "jti-order-3", p => { p["aud"] = "https://elsewhere.example/v1/agents"; p["iat"] = Now - 31; }), Expect.Refused("proof-mismatch", "claim=aud"));
        Add("iat-31s-old", "5", "iat is 31 seconds before the Forum's clock: outside the skew.", rfcEd,
            Valid(rfcEd, "jti-iat-31-old", p => p["iat"] = Now - 31), Expect.Refused("proof-window", "claim=iat"));
        Add("iat-31s-future", "5", "iat is 31 seconds after the Forum's clock: outside the skew.", rfcEd,
            Valid(rfcEd, "jti-iat-31-future", p => p["iat"] = Now + 31), Expect.Refused("proof-window", "claim=iat"));

        // ---- step 6: the nonce -------------------------------------------------------------------------------------------------------------------------------------------
        Add("nonce-not-current", "6", "A nonce the store does not report current.", rfcEd,
            Valid(rfcEd, "jti-nonce-stale", p => p["nonce"] = "a-nonce-the-forum-never-chose"), Expect.Refused("proof-nonce-stale", "claim=nonce"));
        Add("nonce-absent", "6", "A signed payload with no nonce claim: absent is stale, not malformed (the remedy is to fetch a nonce).", rfcEd,
            Valid(rfcEd, "jti-nonce-absent", p => p.Remove("nonce")), Expect.Refused("proof-nonce-stale", "claim=nonce"));
        Add("nonce-not-a-string", "6", "A signed payload whose nonce is the number 1.", rfcEd,
            Valid(rfcEd, "jti-nonce-number", p => p["nonce"] = 1), Expect.Refused("proof-nonce-stale", "claim=nonce"));
        Add("window-before-nonce", "5", "iat is 31 seconds old and the nonce is not current: the window is decided first, and it reads no store.", rfcEd,
            Valid(rfcEd, "jti-order-4", p => { p["iat"] = Now - 31; p["nonce"] = "a-nonce-the-forum-never-chose"; }), Expect.Refused("proof-window", "claim=iat"));

        // ---- step 7: the jti ----------------------------------------------------------------------------------------------------------------------------------------------
        Add("jti-spent", "7", "The proof is valid and its jti is in the spent set.", rfcEd,
            Valid(rfcEd, "jti-already-spent"), Expect.Refused("proof-replayed", "claim=jti"), ctx with { SpentJtis = ["jti-already-spent"] });
        Add("nonce-before-jti", "6", "The nonce is not current and the jti is spent: the nonce is decided first.", rfcEd,
            Valid(rfcEd, "jti-spent-too", p => p["nonce"] = "a-nonce-the-forum-never-chose"), Expect.Refused("proof-nonce-stale", "claim=nonce"), ctx with { SpentJtis = ["jti-spent-too"] });

        Directory.CreateDirectory(outputRoot);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (!written.Add(c.Name))
                throw new InvalidOperationException($"the case name {c.Name} is used twice");
            if (c.Expected.Verdict == "ok")
            {
                var parts = c.Proof!.Split('.');
                if (!c.RequestKey.Verify(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Jwk.Base64UrlDecode(parts[2])))
                    throw new InvalidOperationException($"{c.Name}: an accepted case's signature does not verify under its own key");
            }

            WriteCase(outputRoot, c);
        }

        foreach (var stale in Directory.EnumerateDirectories(outputRoot).Select(Path.GetFileName).Where(n => !written.Contains(n!)))
            Console.WriteLine($"  NOTE: {stale} is on disk and is not a case this generator writes (left alone)");

        Console.WriteLine($"  wrote {cases.Count} enroll-proof cases under {outputRoot}");
        return 0;

        // ----- builders --------------------------------------------------------------------------------------------------------------------------------------------------

        string SizedProof(Signer key, string jti, int target)
        {
            // The padding claim's length is searched until the whole proof is exactly the target; the header may carry a second padding member where the base64 steps skip the target.
            for (var headerPad = 0; headerPad < 4; headerPad++)
            {
                for (var pad = 0; pad < 9000; pad++)
                {
                    var h = Header(key.Alg, key.Kid);
                    if (headerPad > 0) h["cty"] = new string('c', headerPad);
                    var p = Payload(key, jti: jti);
                    p["pad"] = new string('a', pad);
                    var length = B64Len(h.ToJsonString(Compact)) + 1 + B64Len(p.ToJsonString(Compact)) + 1 + B64LenBytes(64);
                    if (length == target) return Compose(h, p, key);
                    if (length > target) break;
                }
            }

            throw new InvalidOperationException($"no padding makes a proof of {target} characters");
        }

        string PaddedHeader(Signer key, string jti)
        {
            // A header whose byte length is not a multiple of three, so that base64 pads it; a cty member of growing length finds one.
            for (var filler = 0; filler < 4; filler++)
            {
                var h = Header(key.Alg, key.Kid);
                if (filler > 0) h["cty"] = new string('c', filler);
                var headerText = h.ToJsonString(Compact);
                var p = Payload(key, jti: jti).ToJsonString(Compact);
                var signature = key.Sign(Encoding.ASCII.GetBytes(B64(headerText) + "." + B64(p)));
                var unpadded = B64(headerText);
                var padding = (4 - unpadded.Length % 4) % 4;
                if (padding == 0) continue;
                return unpadded + new string('=', padding) + "." + B64(p) + "." + Jwk.Base64UrlEncode(signature);
            }

            throw new InvalidOperationException("no header length needs base64 padding");
        }

        string StandardAlphabetSignature(Signer key, string jti)
        {
            // Retry the payload's jti until the signature holds a character that differs between the alphabets (a hyphen or an underscore): then the spelling, not the bytes, is the fault.
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var proof = Valid(key, jti + "-" + attempt);
                var sig = proof.Split('.')[2];
                if (sig.Contains('-', StringComparison.Ordinal) || sig.Contains('_', StringComparison.Ordinal))
                    return string.Join('.', proof.Split('.')[0], proof.Split('.')[1], sig.Replace('-', '+').Replace('_', '/'));
            }

            throw new InvalidOperationException("no signature in 200 attempts has a character the alphabets spell differently");
        }

        string TruncateSignature(Signer key, string jti)
        {
            var h = Header(key.Alg, key.Kid).ToJsonString(Compact);
            var p = Payload(key, jti: jti).ToJsonString(Compact);
            var signature = key.Sign(Encoding.ASCII.GetBytes(B64(h) + "." + B64(p)));
            return B64(h) + "." + B64(p) + "." + Jwk.Base64UrlEncode(signature.AsSpan(0, 63));
        }

        string FlipSignatureBit(string proof)
        {
            var parts = proof.Split('.');
            var signature = Jwk.Base64UrlDecode(parts[2]);
            signature[0] ^= 0x01;
            return parts[0] + "." + parts[1] + "." + Jwk.Base64UrlEncode(signature);
        }

        string Raw(string headerText, string payloadText, Signer key)
        {
            var input = B64(headerText) + "." + B64(payloadText);
            return input + "." + Jwk.Base64UrlEncode(key.Sign(Encoding.ASCII.GetBytes(input)));
        }

        string RawWithSignature(string headerText, string payloadText, byte[] signature) =>
            B64(headerText) + "." + B64(payloadText) + "." + Jwk.Base64UrlEncode(signature);

        string Compose(JsonObject header, JsonObject payload, Signer signer) =>
            Raw(header.ToJsonString(Compact), payload.ToJsonString(Compact), signer);
    }

    private static JsonObject Header(string alg, string kid) => new() { ["alg"] = alg, ["kid"] = kid, ["typ"] = Typ };

    private static string B64(string text) => Jwk.Base64UrlEncode(Encoding.UTF8.GetBytes(text));

    private static int B64Len(string text) => B64LenBytes(Encoding.UTF8.GetByteCount(text));

    private static int B64LenBytes(int bytes) => bytes / 3 * 4 + (bytes % 3 == 0 ? 0 : bytes % 3 + 1);

    private static byte[] FromB64Url(string value) => Jwk.Base64UrlDecode(value);

    private static byte[] Seed(byte b) => Enumerable.Repeat(b, 32).ToArray();

    private static Signer Ed(string name, byte[] seed, string? pinnedJkt = null)
    {
        var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var computed = Thumbprint($$"""{"crv":"Ed25519","kty":"OKP","x":"{{Jwk.Base64UrlEncode(publicKey)}}"}""");
        if (pinnedJkt is not null && !string.Equals(pinnedJkt, computed, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: the published thumbprint {pinnedJkt} is not the key's RFC 7638 thumbprint {computed}");

        return new Signer(
            "EdDSA",
            name == "epv-rfc8037" ? Kid : name,
            publicKey,
            computed,
            data => SignatureAlgorithm.Ed25519.Sign(key, data),
            (data, signature) => SignatureAlgorithm.Ed25519.Verify(key.PublicKey, data, signature));
    }

    private static byte[] Rfc7515PublicX()
    {
        using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = FromB64Url(Rfc7515D) });
        return ecdsa.ExportParameters(false).Q.X!;
    }

    private static Signer Es(byte[] d, string pinnedJkt)
    {
#pragma warning disable CA2000 // the key lives as long as the process: the signer's closures use it
        var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
#pragma warning restore CA2000
        var parameters = ecdsa.ExportParameters(false);
        var computed = Thumbprint($$"""{"crv":"P-256","kty":"EC","x":"{{Jwk.Base64UrlEncode(parameters.Q.X!)}}","y":"{{Jwk.Base64UrlEncode(parameters.Q.Y!)}}"}""");
        if (!string.Equals(Rfc7515Y, Jwk.Base64UrlEncode(parameters.Q.Y!), StringComparison.Ordinal))
            throw new InvalidOperationException("the RFC 7515 scalar does not derive the RFC's public y");
        if (!string.Equals(pinnedJkt, computed, StringComparison.Ordinal))
            throw new InvalidOperationException($"the independently computed thumbprint {pinnedJkt} is not the key's RFC 7638 thumbprint {computed}");

        return new Signer(
            "ES256",
            EsKid,
            ecdsa.ExportSubjectPublicKeyInfo(),
            computed,
            data =>
            {
                var input = Encoding.ASCII.GetString(data);
                if (_esPins.TryGetValue(input, out var pinned) && ecdsa.VerifyData(data, pinned, HashAlgorithmName.SHA256))
                    return pinned;
                return ecdsa.SignData(data, HashAlgorithmName.SHA256);
            },
            (data, signature) => ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256));
    }

    private static string Thumbprint(string requiredMembersJson) =>
        Jwk.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(requiredMembersJson)));

    /// <summary>The signing inputs and signatures of the ES256 proofs already on disk: the pin.</summary>
    private static Dictionary<string, byte[]> ReadEsPins(string outputRoot)
    {
        var pins = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(outputRoot)) return pins;
        foreach (var file in Directory.EnumerateFiles(outputRoot, "input.json", SearchOption.AllDirectories))
        {
            var request = JsonDocument.Parse(File.ReadAllBytes(file)).RootElement.GetProperty("request");
            if (!request.TryGetProperty("proof", out var proof) || proof.GetString() is not { } text) continue;
            var parts = text.Split('.');
            if (parts.Length != 3) continue;
            try
            {
                var header = JsonDocument.Parse(Jwk.Base64UrlDecode(parts[0])).RootElement;
                if (header.ValueKind == JsonValueKind.Object && header.TryGetProperty("alg", out var alg) && alg.GetString() == "ES256")
                    pins[parts[0] + "." + parts[1]] = Jwk.Base64UrlDecode(parts[2]);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                // A proof that does not read is not an ES256 pin.
            }
        }

        return pins;
    }

    private static void WriteCase(string outputRoot, Case c)
    {
        var dir = Path.Combine(outputRoot, c.Name);
        Directory.CreateDirectory(dir);

        var request = new JsonObject
        {
            ["agent_id"] = c.AgentIdOnRequest,
            ["alg"] = c.RequestKey.Alg,
            ["kid"] = c.KidOnRequest,
            ["public_key"] = Convert.ToBase64String(c.RequestKey.StoredPublic),
        };
        if (c.Proof is not null) request["proof"] = c.Proof;

        var context = new JsonObject
        {
            ["audience"] = c.Context.Audience,
            ["current_nonces"] = new JsonArray(c.Context.CurrentNonces.Select(n => (JsonNode?)n).ToArray()),
            ["now"] = c.Context.Now,
            ["spent_jtis"] = new JsonArray(c.Context.SpentJtis.Select(n => (JsonNode?)n).ToArray()),
        };
        Write(dir, "input.json", new JsonObject { ["context"] = context, ["request"] = request });

        var expected = new JsonObject { ["verdict"] = c.Expected.Verdict };
        if (c.Expected.Type is not null) expected["type"] = c.Expected.Type;
        if (c.Expected.Detail is not null) expected["detail"] = c.Expected.Detail;
        Write(dir, "expected.json", expected);

        var meta = new JsonObject
        {
            ["profile"] = "enroll-proof",
            ["requirement"] = Requirement,
            ["step"] = c.Step,
            ["note"] = c.Note + " expected.json is written from this construction and the R4.46 step it targets, never by running a validator. A proposal: it may be regenerated until beta.",
        };
        if (c.Anchors)
        {
            meta["jkt"] = c.RequestKey.Jkt;
            meta["jkt-source"] = c.RequestKey.Alg == "EdDSA"
                ? "RFC 8037 Appendix A (the thumbprint of the Appendix A key, as the RFC prints it)"
                : "RFC 7515 Appendix A.3 key; the thumbprint was computed independently in Python";
            if (c.RequestKey.Alg == "ES256") meta["jkt-method"] = JktMethod;
        }

        Write(dir, "meta.json", meta);
    }

    private static void Write(string dir, string name, JsonObject value) =>
        File.WriteAllText(Path.Combine(dir, name), value.ToJsonString(Jwk.PrettyOptions) + "\n");
}
