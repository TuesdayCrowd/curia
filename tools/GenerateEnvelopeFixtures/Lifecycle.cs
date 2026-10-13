using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon;
using Curia.Canon.Acta;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using NSec.Cryptography;
using JsonValue = Curia.Canon.Json.JsonValue;

namespace GenerateEnvelopeFixtures;

/// <summary>
/// The key lifecycle's conformance/acta/ entries (keys that end, spec 4.5, 4.6). Everything here is
/// deterministic: Ed25519 signs deterministically from fixed seeds, and the P-256 key is derived from a
/// counter, so a rerun writes the same bytes. The vectors are proposals until the owner's lift record
/// pins their digests, and may be regenerated until then.
///
/// <para>Each statement is the exact output of <c>CanonicalizeWithNfc</c>; each signature is under its
/// kind's header <c>typ</c> and names its role's member as the header <c>kid</c> (KF1, KF2); the entry
/// is canonicalized under the pure profile, as every acta/ vector is.</para>
/// </summary>
internal static class Lifecycle
{
    private const string Agent = "agent://curia.example/tuesdaycrowd/scriptor";
    private const string FirstKid = "conformance-ed25519-minimal";
    private const string SecondKid = "conformance-ed25519-second";
    private const string P256Kid = "conformance-es256-x00";

    private const string RotationHeaderTyp = "curia-key-rotation+jws";
    private const string RetirementHeaderTyp = "curia-key-retirement+jws";

    public static int Run(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "conformance", "acta");

        // The key envelope/ed25519-minimal publishes: its private scalar is in that vector's private-keys.json.
        using var firstDoc = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repoRoot, "conformance", "envelope", "ed25519-minimal", "private-keys.json")));
        var firstJwk = firstDoc.RootElement.GetProperty("keys")[0];
        var firstSeed = Jwk.Base64UrlDecode(firstJwk.GetProperty("d").GetString()!);
        var firstPublic = Jwk.Base64UrlDecode(firstJwk.GetProperty("x").GetString()!);

        var secondSeed = Enumerable.Repeat((byte)0x02, 32).ToArray();
        var secondPublic = PublicOf(secondSeed);

        // ---- key-rotated-entry -----------------------------------------------------------------
        var secondJwk = Ed25519Jwk(SecondKid, secondPublic);
        var rotation = JsonBuilders.Obj(
            ("agent_id", JsonBuilders.Str(Agent)),
            ("authorized_by", JsonBuilders.Str(FirstKid)),
            ("jwk", secondJwk),
            ("kid", JsonBuilders.Str(SecondKid)),
            ("prev", JsonBuilders.Num(1)),
            ("typ", JsonBuilders.Str("curia.key-rotation/1")));
        var rotationBytes = CanonicalizeStatement(rotation);
        var authorization = Signing.Sign(rotationBytes, "EdDSA", FirstKid, firstSeed, RotationHeaderTyp);
        var possession = Signing.Sign(rotationBytes, "EdDSA", SecondKid, secondSeed, RotationHeaderTyp);
        RequireVerified(rotationBytes, authorization, FirstKid, firstPublic, RotationHeaderTyp, "rotation authorization");
        RequireVerified(rotationBytes, possession, SecondKid, secondPublic, RotationHeaderTyp, "rotation possession");

        var rotated = Entry(
            "01K4CQ1V9P3R5T7W9Y1A3C5E8H",
            "2026-09-04T15:30:00.000000Z",
            "agent.key-bound",
            JsonBuilders.Obj(
                ("agent_id", JsonBuilders.Str(Agent)),
                ("authorization", JsonBuilders.Str(authorization.Compact)),
                ("authorized_by", JsonBuilders.Str(FirstKid)),
                ("jwk", secondJwk),
                ("kid", JsonBuilders.Str(SecondKid)),
                ("possession", JsonBuilders.Str(possession.Compact)),
                ("prev", JsonBuilders.Num(1)),
                ("statement", JsonBuilders.Str(Encoding.UTF8.GetString(rotationBytes.Span)))));

        // ---- key-retired-entry -----------------------------------------------------------------
        var retirement = JsonBuilders.Obj(
            ("agent_id", JsonBuilders.Str(Agent)),
            ("kid", JsonBuilders.Str(FirstKid)),
            ("prev", JsonBuilders.Num(2)),
            ("typ", JsonBuilders.Str("curia.key-retirement/1")));
        var retirementBytes = CanonicalizeStatement(retirement);
        var retirementSig = Signing.Sign(retirementBytes, "EdDSA", FirstKid, firstSeed, RetirementHeaderTyp);
        RequireVerified(retirementBytes, retirementSig, FirstKid, firstPublic, RetirementHeaderTyp, "retirement authorization");

        var retired = Entry(
            "01K4CQ1V9P3R5T7W9Y1A3C5E9J",
            "2026-09-04T16:00:00.000000Z",
            "agent.key-retired",
            JsonBuilders.Obj(
                ("agent_id", JsonBuilders.Str(Agent)),
                ("authorization", JsonBuilders.Str(retirementSig.Compact)),
                ("kid", JsonBuilders.Str(FirstKid)),
                ("prev", JsonBuilders.Num(2)),
                ("statement", JsonBuilders.Str(Encoding.UTF8.GetString(retirementBytes.Span)))));

        // ---- key-bound-entry-es256 -------------------------------------------------------------
        var (x32, y32, spki) = P256WithLeadingZeroX();
        var p256Jwk = JsonBuilders.Obj(
            ("alg", JsonBuilders.Str("ES256")),
            ("crv", JsonBuilders.Str("P-256")),
            ("kid", JsonBuilders.Str(P256Kid)),
            ("kty", JsonBuilders.Str("EC")),
            ("x", JsonBuilders.Str(Jwk.Base64UrlEncode(x32))),
            ("y", JsonBuilders.Str(Jwk.Base64UrlEncode(y32))));
        var boundEs256 = Entry(
            "01K4CQ1V9P3R5T7W9Y1A3C5EAK",
            "2026-09-04T15:00:00.000000Z",
            "agent.key-bound",
            JsonBuilders.Obj(
                ("agent_id", JsonBuilders.Str(Agent)),
                ("jwk", p256Jwk),
                ("kid", JsonBuilders.Str(P256Kid))));

        const string logNote =
            "The implied log: index 0 agent.enrolled, 1 the enrolment's agent.key-bound (key-bound-entry), "
            + "2 this rotation (key-rotated-entry), 3 the retirement of the enrolled key (key-retired-entry).";
        const string lifecycleNote =
            " The statement is the exact output of CanonicalizeWithNfc, each signature is detached under its kind's "
            + "header typ with its role's member as the header kid (spec 4.5, KF1, KF2). A proposal: it may be "
            + "regenerated until the owner's lift record pins its digests. Expected values were computed with "
            + "python3's json and hashlib, outside both implementations.";

        Write(root, "key-rotated-entry", rotated, "R4.38",
            "An agent.key-bound event in its rotation form (R4.38, spec 4.6): the enrolled key conformance-ed25519-minimal "
            + "authorizes a second key, conformance-ed25519-second (seed 0x02 repeated 32 times), which signs possession of "
            + "its own binding. prev is 1. " + logNote + lifecycleNote);
        Write(root, "key-retired-entry", retired, "R4.38",
            "An agent.key-retired event (R4.38, spec 4.6): conformance-ed25519-minimal retires itself, signed by itself. "
            + "prev is 2. " + logNote + lifecycleNote);
        Write(root, "key-bound-entry-es256", boundEs256, "R4.34",
            "An agent.key-bound event as an enrolment appends it, for a P-256 key whose x coordinate begins with the byte "
            + "0x00 (milestone M6, owed since the key-binding stage): a coordinate that loses its leading zero is the "
            + "encoding bug this vector exists to catch. The key is derived from a counter, so it is reproducible; its "
            + "SubjectPublicKeyInfo is in meta.json.",
            extra: new JsonObject { ["spki"] = Convert.ToBase64String(spki) });

        Console.WriteLine("lifecycle vectors written under conformance/acta/");
        return 0;
    }

    private static JsonValue Ed25519Jwk(string kid, byte[] publicKey) => JsonBuilders.Obj(
        ("alg", JsonBuilders.Str("EdDSA")),
        ("crv", JsonBuilders.Str("Ed25519")),
        ("kid", JsonBuilders.Str(kid)),
        ("kty", JsonBuilders.Str("OKP")),
        ("x", JsonBuilders.Str(Jwk.Base64UrlEncode(publicKey))));

    private static byte[] PublicOf(byte[] seed)
    {
        using var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    private static CanonicalBytes CanonicalizeStatement(JsonValue statement) =>
        CanonicalJson.CanonicalizeWithNfc(statement)
            .Match(b => b, e => throw new InvalidOperationException($"canonicalize failed: {e.Type}"));

    private static void RequireVerified(
        CanonicalBytes statement, JwsSignature sig, string kid, byte[] publicKey, string typ, string what)
    {
        if (!Signing.Verify(statement, sig, "EdDSA", kid, publicKey, typ).IsOk)
            throw new InvalidOperationException($"{what} did not verify under its own key and typ");
    }

    private static JsonValue Entry(string eventId, string serverTs, string eventType, JsonValue payload) => JsonBuilders.Obj(
        ("actor_id", JsonBuilders.Str(Agent)),
        ("aggregate_id", JsonBuilders.Str(Agent)),
        ("event_id", JsonBuilders.Str(eventId)),
        ("event_type", JsonBuilders.Str(eventType)),
        ("payload", payload),
        ("server_ts", JsonBuilders.Str(serverTs)));

    /// <summary>The first P-256 key, over a counter-derived scalar, whose x coordinate begins with 0x00.</summary>
    private static (byte[] X, byte[] Y, byte[] Spki) P256WithLeadingZeroX()
    {
        for (var counter = 0; counter < 100_000; counter++)
        {
            var d = SHA256.HashData(Encoding.UTF8.GetBytes($"curia-conformance-p256-x00-{counter}"));
            ECParameters parameters;
            try
            {
                using var candidate = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
                parameters = candidate.ExportParameters(includePrivateParameters: false);
            }
            catch (CryptographicException)
            {
                continue; // a scalar outside [1, n-1]
            }

            if (parameters.Q.X![0] != 0x00)
                continue;

            using var key = ECDsa.Create(parameters);
            return (parameters.Q.X, parameters.Q.Y!, key.ExportSubjectPublicKeyInfo());
        }

        throw new InvalidOperationException("no P-256 key with a leading-zero x coordinate within the search bound");
    }

    private static void Write(string root, string name, JsonValue entry, string requirement, string note, JsonObject? extra = null)
    {
        var canonical = CanonicalJson.Canonicalize(entry)
            .Match(b => b, e => throw new InvalidOperationException($"canonicalize failed: {e.Type}"));
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        File.WriteAllBytes(Path.Combine(dir, "input.json"), canonical.ToArray());
        File.WriteAllBytes(Path.Combine(dir, "expected.canonical"), canonical.ToArray());
        File.WriteAllText(Path.Combine(dir, "expected.digest"), Digests.Sha256(canonical).ToHex() + "\n");
        File.WriteAllText(
            Path.Combine(dir, "expected.leaf"),
            Convert.ToHexStringLower([.. MerkleTree.LeafHash(canonical.Span)]) + "\n");

        var meta = new JsonObject { ["profile"] = "acta-leaf", ["requirement"] = requirement, ["note"] = note };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                meta[key] = value?.DeepClone();
        }
        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString(Jwk.PrettyOptions) + "\n");
        Console.WriteLine($"  wrote acta/{name}");
    }
}
