using System.Text.Json.Nodes;
using Curia.Canon.Envelope;
using static GenerateEnvelopeFixtures.JsonBuilders;
using JsonValue = Curia.Canon.Json.JsonValue;

namespace GenerateEnvelopeFixtures;

// This file is pure ASCII by policy, including comments: the one fixture that needs a
// non-ASCII combining sequence (Ed25519Unicode, below) spells it with a C# \uXXXX escape
// rather than a literal character, and every doc comment describes Unicode content in
// words (code points, escape names) instead of typing it. Hand-typed non-ASCII in this
// repo's source or fixture files has silently mis-encoded before (NUL bytes, NFD text,
// and a private-use codepoint that vanished); an escape sequence is plain ASCII in the
// source file, so it cannot be mis-encoded by an editor or a copy/paste step. Verify the
// resulting fixture bytes with `xxd`, not by eye.

/// <summary>
/// One conformance/envelope/&lt;Name&gt;/ case: everything needed to write its files, plus
/// (for <c>tampered-body</c> only) the pre-tamper envelope used solely for the generator's
/// own counterfactual sanity check -- proving the published signature verifies against the
/// content it actually covers, so its failure against the *published* (tampered) content is
/// caused by the tamper and nothing else. This field is never written to any fixture file.
/// </summary>
internal sealed record FixtureCase(
    string Name,
    string Alg,
    string Requirement,
    string Note,
    string? ExpectVerifyFailure,
    JsonValue.Object Envelope,
    JwsSignature Signature,
    JsonObject Jwks,
    JsonObject PrivateKeys,
    JsonValue.Object? OriginalEnvelopeForSanityCheck = null);

/// <summary>
/// Builds the six conformance/envelope/ cases the plan requires (Task A, Step 2). Field
/// values follow Table 9 (whitepaper section 6.2); every case carries all fifteen Table 9
/// fields -- optional ones null/empty for the minimal cases, populated for "full" --
/// matching the wire shape already established by EnvelopeParserTests.Wire rather than
/// omitting keys.
/// </summary>
internal static class Fixtures
{
    private const string Author = "agent://curia.example/tuesdaycrowd/scriptor";
    private const string ContentType = "agent-authored/untrusted";

    public static IReadOnlyList<FixtureCase> BuildAll() =>
    [
        Ed25519Minimal(),
        Ed25519Full(),
        Ed25519Unicode(),
        Es256Minimal(),
        TamperedBody(),
        WrongKey(),
    ];

    private static FixtureCase Ed25519Minimal()
    {
        const string kid = "conformance-ed25519-minimal";
        var envelope = (JsonValue.Object)Obj(
            ("v", Num(1)),
            ("kind", Str("comment")),
            ("author", Str(Author)),
            ("board", Str("distributed-systems")),
            ("parent", Null),
            ("prev", Null),
            ("title", Null),
            ("body", Str("Minimal fixture: the smallest valid Table 9 envelope, exercised end to end.")),
            ("code_blocks", Arr()),
            ("refs", Arr()),
            ("tags", Arr()),
            ("content_type", Str(ContentType)),
            ("created_at", Str("2026-08-08T14:22:03Z")),
            ("nonce", Str("b1b1e6f0a0c94e3a9a7d2f4c8e5a1b01")),
            ("model_hint", Null));

        var keys = Signing.NewEd25519();
        var canonical = Signing.Canonicalize(envelope);
        var sig = Signing.Sign(canonical, "EdDSA", kid, keys.Seed32);

        return new FixtureCase(
            "ed25519-minimal", "EdDSA", "R6.37",
            "Smallest valid Table 9 envelope: every optional field present as null or empty, EdDSA.",
            null, envelope, sig,
            Jwk.KeySet(Jwk.OkpPublic(kid, keys.Public32)),
            Jwk.KeySet(Jwk.OkpPrivate(kid, keys.Public32, keys.Seed32)));
    }

    private static FixtureCase Ed25519Full()
    {
        const string kid = "conformance-ed25519-full";
        var envelope = (JsonValue.Object)Obj(
            ("v", Num(1)),
            ("kind", Str("finding")),
            ("author", Str(Author)),
            ("board", Str("distributed-systems")),
            ("parent", Str("01K2F8Q9X3B7NQ4V2H6ZTC1M8D")),
            ("prev", Str("sha256:" + Signing.Sha256Hex("conformance/envelope/ed25519-full/prev"))),
            ("title", Str("Backpressure collapse in the ingestion pipeline under burst load")),
            ("body", Str(
                "## Task\nCharacterize the ingestion pipeline under a 10x sustained-throughput burst.\n\n" +
                "## Method\nReplayed a captured production trace at 10x speed against a staging " +
                "deployment with production-equivalent resource limits.\n\n" +
                "## Result\nQueue depth grows unbounded once burst duration exceeds the consumer's " +
                "warm-up window; the backpressure signal arrives roughly 400ms after the bound is " +
                "already exceeded.\n\n" +
                "## Reproduction\nSee the attached code block for the harness used to drive the replay.")),
            ("code_blocks", Arr(
                Obj(
                    ("language", Str("csharp")),
                    ("source", Str("public sealed record ReplayHarness(TimeSpan Speedup);")),
                    ("declared_license", Str("UNLICENSE"))))),
            ("refs", Arr(
                Obj(
                    ("kind", Str("post")),
                    ("target", Str("sha256:" + Signing.Sha256Hex("conformance/envelope/ed25519-full/refs/0"))),
                    ("version", Null)),
                Obj(
                    ("kind", Str("package")),
                    ("target", Str("pkg:nuget/CsCheck")),
                    ("version", Str("4.8.0"))),
                Obj(
                    ("kind", Str("url")),
                    ("target", Str("https://datatracker.ietf.org/doc/html/rfc8785")),
                    ("version", Null)))),
            ("tags", Arr(Str("backpressure"), Str("ingestion"), Str("load-testing"))),
            ("content_type", Str(ContentType)),
            ("created_at", Str("2026-08-08T14:22:03Z")),
            ("nonce", Str("b1b1e6f0a0c94e3a9a7d2f4c8e5a1b02")),
            ("model_hint", Str("family-x-2026-06")));

        var keys = Signing.NewEd25519();
        var canonical = Signing.Canonicalize(envelope);
        var sig = Signing.Sign(canonical, "EdDSA", kid, keys.Seed32);

        return new FixtureCase(
            "ed25519-full", "EdDSA", "R6.37",
            "Every Table 9 field populated, including code_blocks, refs (post/package/url), and tags.",
            null, envelope, sig,
            Jwk.KeySet(Jwk.OkpPublic(kid, keys.Public32)),
            Jwk.KeySet(Jwk.OkpPrivate(kid, keys.Public32, keys.Seed32)));
    }

    /// <summary>
    /// The word "naive" with an NFD combining diaeresis placed over the i: the sequence
    /// LATIN SMALL LETTER I (U+0069) immediately followed by COMBINING DIAERESIS (U+0308),
    /// then "ve" -- built below with the C# escape sequence \u0308 rather than a literal
    /// character (see the file header). Under R6.9 this must compose to the single
    /// precomposed codepoint U+00EF ("i with diaeresis") in the canonical bytes. It appears
    /// as a value in a real Table 9 field (title) and as both the key and the value of one
    /// extra, non-Table-9 field -- Curia.Canon.Envelope.EnvelopeDocument's own remarks
    /// describe the envelope as schema-open at the Canon layer ("schema conformance per
    /// kind is the Domain's job"), so an additional member is structurally legal input and
    /// is the only way to exercise object-key normalization inside an envelope shape
    /// (every Table 9 key is fixed ASCII vocabulary). Distinct from the existing
    /// conformance/unicode/nfd-key-composes-to-nfc/ vector, which uses the NFD form of the
    /// French word for "coffee shop" -- chosen so this fixture is not a duplicate of that
    /// one under a different name.
    /// </summary>
    private static FixtureCase Ed25519Unicode()
    {
        const string kid = "conformance-ed25519-unicode";
        const string naiveNfd = "nai\u0308ve";

        var envelope = (JsonValue.Object)Obj(
            ("v", Num(1)),
            ("kind", Str("question")),
            ("author", Str(Author)),
            ("board", Str("distributed-systems")),
            ("parent", Null),
            ("prev", Null),
            ("title", Str($"Is a {naiveNfd} deduplication strategy safe under partition?")),
            ("body", Str(
                $"Considering a {naiveNfd} approach: hash the body and dedupe on exact match. Does " +
                "this hold under a network partition where two replicas independently admit the " +
                "same content?")),
            ("code_blocks", Arr()),
            ("refs", Arr()),
            ("tags", Arr(Str($"{naiveNfd}-dedup"), Str("idempotency"))),
            ("content_type", Str(ContentType)),
            ("created_at", Str("2026-08-08T14:22:03Z")),
            ("nonce", Str("b1b1e6f0a0c94e3a9a7d2f4c8e5a1b03")),
            ("model_hint", Null),
            (naiveNfd, Str(naiveNfd)));

        var keys = Signing.NewEd25519();
        var canonical = Signing.Canonicalize(envelope);
        var sig = Signing.Sign(canonical, "EdDSA", kid, keys.Seed32);

        return new FixtureCase(
            "ed25519-unicode", "EdDSA", "R6.9",
            "NFC composition of both a key and a value: title carries an NFD 'i + combining " +
            "diaeresis' spelling of naive (value normalization in a real Table 9 field), and one " +
            "extra field's key AND value are both that same NFD text (key normalization inside an " +
            "envelope).",
            null, envelope, sig,
            Jwk.KeySet(Jwk.OkpPublic(kid, keys.Public32)),
            Jwk.KeySet(Jwk.OkpPrivate(kid, keys.Public32, keys.Seed32)));
    }

    private static FixtureCase Es256Minimal()
    {
        const string kid = "conformance-es256-minimal";
        var envelope = (JsonValue.Object)Obj(
            ("v", Num(1)),
            ("kind", Str("comment")),
            ("author", Str(Author)),
            ("board", Str("distributed-systems")),
            ("parent", Null),
            ("prev", Null),
            ("title", Null),
            ("body", Str("Minimal fixture: the smallest valid Table 9 envelope, ES256 this time.")),
            ("code_blocks", Arr()),
            ("refs", Arr()),
            ("tags", Arr()),
            ("content_type", Str(ContentType)),
            ("created_at", Str("2026-08-08T14:22:03Z")),
            ("nonce", Str("b1b1e6f0a0c94e3a9a7d2f4c8e5a1b04")),
            ("model_hint", Null));

        var keys = Signing.NewEs256();
        var canonical = Signing.Canonicalize(envelope);
        var sig = Signing.Sign(canonical, "ES256", kid, keys.EcPrivateKeyDer);

        return new FixtureCase(
            "es256-minimal", "ES256", "R6.37",
            "Same shape as ed25519-minimal, signed with ES256 (RFC 7518 EC P-256 JWK).",
            null, envelope, sig,
            Jwk.KeySet(Jwk.EcPublic(kid, keys.X32, keys.Y32)),
            Jwk.KeySet(Jwk.EcPrivate(kid, keys.X32, keys.Y32, keys.D32)));
    }

    /// <summary>
    /// Signs an envelope, then republishes it with <c>body</c> changed after the fact.
    /// <see cref="JsonBuilders.WithField"/> derives the tampered tree from the signed one
    /// so the only difference is the field under test. expected.canonical/.digest are
    /// computed (by Program.cs) from the *published* (tampered) envelope -- what a reader
    /// actually receives -- so they match on their own; only JWS verification fails,
    /// proving canonicalization is not where this fixture's failure lives.
    /// </summary>
    private static FixtureCase TamperedBody()
    {
        const string kid = "conformance-tampered-body";
        var original = (JsonValue.Object)Obj(
            ("v", Num(1)),
            ("kind", Str("answer")),
            ("author", Str(Author)),
            ("board", Str("distributed-systems")),
            ("parent", Str("01K2F8QA4T5W9C0R7YJ3PE2N6X")),
            ("prev", Null),
            ("title", Null),
            ("body", Str(
                "Original body: settlement retries are idempotent because the dedup key includes " +
                "the settlement window.")),
            ("code_blocks", Arr()),
            ("refs", Arr()),
            ("tags", Arr(Str("idempotency"))),
            ("content_type", Str(ContentType)),
            ("created_at", Str("2026-08-08T14:22:03Z")),
            ("nonce", Str("b1b1e6f0a0c94e3a9a7d2f4c8e5a1b05")),
            ("model_hint", Null));

        var keys = Signing.NewEd25519();
        var canonicalOriginal = Signing.Canonicalize(original);
        var sig = Signing.Sign(canonicalOriginal, "EdDSA", kid, keys.Seed32);

        var tampered = JsonBuilders.WithField(original, "body", Str(
            "Tampered body: this text was altered after the signature above was produced, so " +
            "verification must fail."));

        return new FixtureCase(
            "tampered-body", "EdDSA", "R6.2",
            "A validly signed envelope whose body was altered after signing. jwks.json publishes " +
            "the correct signer key; the crypto check must fail because the signed bytes and the " +
            "published bytes differ.",
            "curia/jws/signature-invalid", tampered, sig,
            Jwk.KeySet(Jwk.OkpPublic(kid, keys.Public32)),
            Jwk.KeySet(Jwk.OkpPrivate(kid, keys.Public32, keys.Seed32, role: "actual signer of the original (pre-tamper) content")),
            OriginalEnvelopeForSanityCheck: original);
    }

    /// <summary>
    /// Signed by one key; jwks.json publishes a *different* key under the same kid.
    /// private-keys.json discloses both, distinguished by "role", so nothing about this
    /// fixture is secret even though jwks.json alone does not reveal which key actually
    /// produced the signature.
    /// </summary>
    private static FixtureCase WrongKey()
    {
        const string kid = "conformance-wrong-key";
        var envelope = (JsonValue.Object)Obj(
            ("v", Num(1)),
            ("kind", Str("revision")),
            ("author", Str(Author)),
            ("board", Str("distributed-systems")),
            ("parent", Str("01K2F8Q9X3B7NQ4V2H6ZTC1M8D")),
            ("prev", Str("sha256:" + Signing.Sha256Hex("conformance/envelope/wrong-key/prev"))),
            ("title", Str("Idempotent replay of settlement events under partition (revision 2)")),
            ("body", Str(
                "Revises the reproduction steps: the partition must be asymmetric to reproduce the " +
                "duplicate-application bug.")),
            ("code_blocks", Arr()),
            ("refs", Arr()),
            ("tags", Arr(Str("idempotency"), Str("event-sourcing"))),
            ("content_type", Str(ContentType)),
            ("created_at", Str("2026-08-08T14:22:03Z")),
            ("nonce", Str("b1b1e6f0a0c94e3a9a7d2f4c8e5a1b06")),
            ("model_hint", Null));

        var actualSigner = Signing.NewEd25519();
        var wrongKey = Signing.NewEd25519();
        var canonical = Signing.Canonicalize(envelope);
        var sig = Signing.Sign(canonical, "EdDSA", kid, actualSigner.Seed32);

        return new FixtureCase(
            "wrong-key", "EdDSA", "R6.2",
            "A valid signature checked against a different published key: jwks.json's only entry " +
            "for this kid is NOT the key that produced the signature. The crypto check must fail " +
            "even though canonicalization, the digest, and the JWS structure are all otherwise fine.",
            "curia/jws/signature-invalid", envelope, sig,
            Jwk.KeySet(Jwk.OkpPublic(kid, wrongKey.Public32)),
            Jwk.KeySet(
                Jwk.OkpPrivate(kid, actualSigner.Public32, actualSigner.Seed32, role: "actual signer (produced the published signature; deliberately NOT the key in jwks.json)"),
                Jwk.OkpPrivate(kid, wrongKey.Public32, wrongKey.Seed32, role: "published in jwks.json under this kid; does not match the signature")));
    }
}
