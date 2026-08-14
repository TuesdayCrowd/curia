using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using CsCheck;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Xunit;

namespace Curia.Canon.Tests.Properties;

/// <summary>
/// R14.1 properties P1-P5. These must hold for all generated inputs, not just the
/// examples someone thought to write down -- that is the entire reason this suite is
/// property-based rather than example-based.
///
/// Generator honesty: three categories of input are deliberately excluded, and all three
/// are documented here rather than silently dropped:
///
///  - Unpaired surrogates. <see cref="JsonReader.Parse"/> rejects them at ADMIT
///    (curia/admit/unpaired-surrogate), so a string containing one is not a document the
///    system ever accepts -- generating it would be testing a case ADMIT already screens
///    out, not weakening the property to dodge a real failure. <see cref="HasUnpairedSurrogate"/>
///    filters every string generator below, including P5's, whose own string generator
///    does not round-trip through JsonReader.Parse but would otherwise let .Normalize()
///    and the reader disagree on ill-formed UTF-16 for reasons that have nothing to do
///    with the NFC property being tested.
///  - Unicode noncharacters (Unicode 16.0 §23.7). <see cref="JsonReader.Parse"/> rejects
///    these too (curia/admit/noncharacter), so a string containing one is not a document
///    ADMIT ever admits from real wire input -- excluding it here keeps this suite scoped
///    to what the real ingest pipeline can produce, even though it is no longer excluded
///    for correctness: a property test run (seed 01AW7nJDoii1) once generated a string
///    containing U+FFFE and P5 crashed with ArgumentException from string.Normalize(FormC)
///    -- .NET/ICU reads that one noncharacter as a reversed byte-order mark. That crash was
///    real evidence of a production defect, originally fixed only at ADMIT (JsonReader
///    rejects the code point before a JsonValue exists to reach CanonicalizeWithNfc at
///    all). R6.38 (errata E2) later required CanonicalizeWithNfc itself to accept and
///    correctly canonicalize a noncharacter when a caller reaches it without ADMIT having
///    run -- e.g. via <see cref="JsonReader.ParseUnrestricted"/>, which P3 below now uses --
///    so NormalizeString was hardened a second time to succeed on U+FFFE rather than merely
///    fail without crashing (CanonicalJsonTests.CanonicalizeWithNfcAcceptsANoncharacterWhen-
///    AdmitIsBypassed pins this directly). See JsonReader.IsNoncharacter and
///    CanonicalJsonTests.AdmitRejectsNoncharacterBeforeCanonicalizationCanEverSeeIt for the
///    ADMIT-side rejection this suite's own exclusion mirrors. <see cref="HasNoncharacter"/>
///    filters every generator below regardless, because these generators construct
///    JsonValue trees directly and so bypass JsonReader.Parse/ParseUnrestricted entirely
///    when first built -- P3 is the only property that later reparses at all (via
///    ParseUnrestricted, not the ADMIT-gated Parse: reparsing canonical output to test
///    idempotency is canonicalization's own concern, not ADMIT's), but every generator
///    below filters consistently rather than only the one property that would otherwise
///    visibly differ.
///  - Duplicate object keys. <see cref="JsonReader.Parse"/> rejects them too
///    (curia/admit/duplicate-key), and this remains a well-definedness rule R6.38 does not
///    exempt (RFC 8785 defines no canonical output for a duplicate member name -- see
///    JsonReader.ParseUnrestricted's remarks). <see cref="GenValue"/> deduplicates generated
///    member lists by key before building a <see cref="JsonValue.Object"/> so P3's reparse
///    step never sees a document neither parse path would admit.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test method names carry R14.1's own property numbering (P1-P5) verbatim, " +
        "so a reader can trace 'P3' in the specification straight to 'P3_...' here without a " +
        "second naming scheme to translate through.")]
public sealed class CanonProperties
{
    /// <summary>Every char, including lone surrogate halves -- see <see cref="HasUnpairedSurrogate"/>.</summary>
    private static readonly Gen<char> GenUnicodeChar = Gen.Char[char.MinValue, char.MaxValue];

    /// <summary>
    /// True when <paramref name="s"/> contains a UTF-16 code unit that is not part of a
    /// well-formed surrogate pair. See the type-level remarks for why every generator
    /// below excludes these.
    /// </summary>
    private static bool HasUnpairedSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 == s.Length || !char.IsLowSurrogate(s[i + 1]))
                    return true;
                i++; // skip the low surrogate: it is legitimately paired
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="s"/> contains a Unicode noncharacter (Unicode 16.0 §23.7).
    /// Only ever called on strings <see cref="HasUnpairedSurrogate"/> has already cleared,
    /// so EnumerateRunes here is always operating on well-formed UTF-16. Reuses
    /// <see cref="JsonReader.IsNoncharacter"/> (internal; this assembly has
    /// InternalsVisibleTo) rather than a second hand-rolled definition of the same 66 code
    /// points, so the generator can never quietly drift from what ADMIT actually rejects.
    /// </summary>
    private static bool HasNoncharacter(string s)
    {
        foreach (var rune in s.EnumerateRunes())
        {
            if (JsonReader.IsNoncharacter(rune.Value))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when JsonReader.Parse would reject a string containing <paramref name="s"/> --
    /// see the type-level remarks for why every string generator below filters through
    /// this rather than letting ADMIT-illegal input reach a property.
    /// </summary>
    private static bool IsAdmitRejectedString(string s) => HasUnpairedSurrogate(s) || HasNoncharacter(s);

    private static readonly Gen<string> GenKey =
        Gen.String[GenUnicodeChar, 0, 8].Where(s => !IsAdmitRejectedString(s));

    private static Gen<JsonValue> GenValue(int depth) =>
        depth <= 0
            ? Gen.OneOf<JsonValue>(
                GenKey.Select(s => (JsonValue)new JsonValue.String(s)),
                Gen.Int[-1000, 1000].Select(i => (JsonValue)new JsonValue.Number(i)),
                Gen.Bool.Select(b => (JsonValue)new JsonValue.Bool(b)),
                Gen.Const((JsonValue)JsonValue.Null.Instance))
            : Gen.OneOf(
                GenValue(0),
                Gen.Select(GenKey, GenValue(depth - 1), (k, v) => (Key: k, Value: v))
                   .List[0, 5]
                   .Select(items => (JsonValue)new JsonValue.Object(
                       items.DistinctBy(i => i.Key)
                            .Select(i => new KeyValuePair<string, JsonValue>(i.Key, i.Value))
                            .ToImmutableArray())),
                GenValue(depth - 1).List[0, 4].Select(items => (JsonValue)new JsonValue.Array([.. items])));

    private static readonly Gen<JsonValue> GenJson = GenValue(3);

    /// <summary>Pure RFC 8785 (no NFC). Target for P1-P4: general canonicalization properties.</summary>
    private static byte[] Canon(JsonValue v) =>
        CanonicalJson.Canonicalize(v).Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));

    private static CanonicalBytes CanonBytes(JsonValue v) =>
        CanonicalJson.Canonicalize(v).Match(b => b, e => throw new Xunit.Sdk.XunitException(e.Type));

    /// <summary>The Cūria NFC profile. Target for P5 only: pure Canonicalize does not normalize.</summary>
    private static byte[] CanonNfc(JsonValue v) =>
        CanonicalJson.CanonicalizeWithNfc(v).Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));

    [Fact]
    public void P1_SignThenVerifyAlwaysSucceeds()
    {
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { ["EdDSA"] = new Ed25519Adapter() },
            new Dictionary<string, IContentVerifier> { ["EdDSA"] = new Ed25519Adapter() });

        using var key = NSec.Cryptography.Key.Create(
            NSec.Cryptography.SignatureAlgorithm.Ed25519,
            new NSec.Cryptography.KeyCreationParameters
            { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });

        var signing = new SigningKey("EdDSA", "k", key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey));
        var pub = new PublicKeyMaterial("EdDSA", "k", key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));

        GenJson.Sample(v =>
        {
            var canonical = CanonBytes(v);
            var sig = jws.Sign(canonical, signing).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
            return jws.Verify(canonical, sig, pub).IsOk;
        }, iter: 500);
    }

    [Fact]
    public void P2_AnySingleFieldMutationBreaksVerification()
    {
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { ["EdDSA"] = new Ed25519Adapter() },
            new Dictionary<string, IContentVerifier> { ["EdDSA"] = new Ed25519Adapter() });

        using var key = NSec.Cryptography.Key.Create(
            NSec.Cryptography.SignatureAlgorithm.Ed25519,
            new NSec.Cryptography.KeyCreationParameters
            { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });

        var signing = new SigningKey("EdDSA", "k", key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey));
        var pub = new PublicKeyMaterial("EdDSA", "k", key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));

        Gen.Select(GenKey, Gen.Int[0, 1000], Gen.Int[0, 1000])
           .Where((k, a, b) => a != b)
           .Sample((k, a, b) =>
           {
               var original = new JsonValue.Object([new(k, new JsonValue.Number(a))]);
               var mutated  = new JsonValue.Object([new(k, new JsonValue.Number(b))]);
               var sig = jws.Sign(CanonBytes(original), signing).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
               return !jws.Verify(CanonBytes(mutated), sig, pub).IsOk;
           }, iter: 500);
    }

    [Fact]
    public void P3_CanonicalizationIsIdempotent() =>
        GenJson.Sample(v =>
        {
            var once = Canon(v);
            // R6.41: reparsing canonical output to test idempotency is canonicalization's own
            // concern, not ADMIT's -- ParseUnrestricted, not the ADMIT-gated Parse.
            var reparsed = JsonReader.ParseUnrestricted(once).Match(x => x, e => throw new Xunit.Sdk.XunitException(e.Type));
            return Canon(reparsed).AsSpan().SequenceEqual(once);
        }, iter: 1000);

    [Fact]
    public void P4_CanonicalizationIsOrderIndependent() =>
        GenJson.Sample(v =>
        {
            if (v is not JsonValue.Object o || o.Members.Length < 2) return true;
            var shuffled = new JsonValue.Object([.. o.Members.Reverse()]);
            return Canon(shuffled).AsSpan().SequenceEqual(Canon(o));
        }, iter: 1000);

    [Fact]
    public void P5_CanonicalizationIsUnicodeStable() =>
        Gen.String[GenUnicodeChar, 0, 20].Where(s => !IsAdmitRejectedString(s)).Sample(s =>
        {
            var nfd = new JsonValue.Object([new("k", new JsonValue.String(s.Normalize(NormalizationForm.FormD)))]);
            var nfc = new JsonValue.Object([new("k", new JsonValue.String(s.Normalize(NormalizationForm.FormC)))]);
            return CanonNfc(nfd).AsSpan().SequenceEqual(CanonNfc(nfc));
        }, iter: 1000);
}
