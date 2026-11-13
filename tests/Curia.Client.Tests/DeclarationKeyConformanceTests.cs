using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR H (errata G67, R6.62; Amendment 1 rulings 12 and 26): the declaration key's vectors, run by the client's reader. Each fact names the case it is about and what the
/// reader must make of it, by hand, from the plan's own table: the case's <c>expected.json</c> is written by the generator, so a fact that read its expectation from the file
/// would agree with whatever the generator wrote. A case the family does not hold yet is a red fact that says so (Task 3 generates it); <c>curia-testis</c> runs the same names
/// in <c>rust/curia-testis/tests/declaration_keys.rs</c>, from the same table and not from this file.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DeclarationKeyConformanceTests
{
    private static string Describe(string name, string verdict, string predicate)
    {
        var dir = Path.Combine(LifecycleConformanceTests.Root, name);
        if (!Directory.Exists(dir)) return $"{name}: the case is not in conformance/lifecycle/ (Task 3 generates it)";

        var (gotVerdict, gotPredicate) = LifecycleConformanceTests.Judge(name);
        return (gotVerdict, gotPredicate) == (verdict, predicate) ? string.Empty : $"{name}: expected {verdict} {predicate}, got {gotVerdict} {gotPredicate}";
    }

    private static void AllHold(params string[] problems)
    {
        var failed = problems.Where(p => p.Length > 0).ToList();
        Assert.True(failed.Count == 0, string.Join("; ", failed));
    }

    private static int DirectoryCount(string family)
    {
        var root = family == "lifecycle" ? LifecycleConformanceTests.Root : Path.Combine(LifecycleConformanceTests.Root, "..", family);
        return Directory.EnumerateDirectories(root).Count();
    }

    private static int IndexCount(string family)
    {
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(LifecycleConformanceTests.Root, "..", "index.json")));
        return index.RootElement.GetProperty("directories").EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == family)
            .GetProperty("count").GetInt32();
    }

    /// <summary>
    /// HSU18 (this reader's half). The family holds the 82 lifecycle cases (Amendment 1 ruling 26's 58, the control of HSU22, the six of HSU44, the six of HSU49, the four of HSU51, the six of HSU52 and the one of Amendment 7) and the 12 acta cases, and the index declares
    /// what the directories hold. Red on the seam: <c>lifecycle 53</c>. (Every case reaching its own expected verdict is the existing <c>R6_54_EveryLifecycleCaseReachesItsVerdictAndNamesItsPredicate</c>.)
    /// </summary>
    [Fact]
    public void R6_62_HSU18_TheFamiliesHoldTheCountsOfTheDeclarationKeyVectors()
    {
        Assert.Equal(
            "lifecycle 82 (index 82); acta 12 (index 12)",
            $"lifecycle {DirectoryCount("lifecycle")} (index {IndexCount("lifecycle")}); acta {DirectoryCount("acta")} (index {IndexCount("acta")})");
    }

    /// <summary>HSU19. A declaration signed by a log key is <b>failed</b> and never disputed: a log key never declares (finding 5: the post's lifecycle fails, and the failure is recorded, not hidden).</summary>
    [Fact]
    public void R6_62_HSU19_ADeclarationSignedByALogKeyFailsAndNeverDisputes() =>
        AllHold(Describe("compromise-signed-by-log-key", "failed", "declaration-key-unpublished"));

    /// <summary>HSU20. A <c>/1</c> statement is could not be checked, whoever signed it, and never disputes.</summary>
    [Fact]
    public void R6_62_HSU20_AVersionOneStatementIsCouldNotBeChecked() =>
        AllHold(Describe("compromise-v1-statement", "could-not-check", "unknown-lifecycle-kind"));

    /// <summary>HSU21. A declaration key that is also a log key fails.</summary>
    [Fact]
    public void R6_62_HSU21_ADeclarationKeyThatIsAlsoALogKeyFails() =>
        AllHold(Describe("compromise-declaration-key-is-log-key", "failed", "declaration-key-is-log-key"));

    /// <summary>
    /// HSU22. A declaration under a declaration key that is not the first the log published fails, however the key set is ordered; the same log with the declaration under the first key
    /// disputes (the control, a vector the plan's list did not name: <c>compromise-first-of-two-declaration-keys</c>, so that a reader refusing every declaration of a two-key log is red).
    /// </summary>
    [Fact]
    public void R6_62_HSU22_ADeclarationUnderASecondDeclarationKeyFailsAndUnderTheFirstDisputes() =>
        AllHold(
            Describe("compromise-second-declaration-key", "failed", "declaration-key-not-first"),
            Describe("compromise-first-of-two-declaration-keys", "disputed", "disputed"));

    /// <summary>HSU23. A declaration key published at or after the declaration fails; one whose position the key set does not give is could not be checked.</summary>
    [Fact]
    public void R6_62_HSU23_ADeclarationKeyPublishedTooLateFailsAndOneWithNoPositionCannotBeChecked() =>
        AllHold(
            Describe("compromise-declaration-kid-not-valid-at-position", "failed", "declaration-key-unpublished"),
            Describe("compromise-declaration-kid-position-unknown", "could-not-check", "declaration-key-position-unknown"));

    /// <summary>
    /// HSU24, the control that the new path is not a vacuous refusal: a <c>/2</c> declaration under the first published declaration key disputes the post. The case's declaration
    /// must be a <c>/2</c> statement and the key set it is read against must serve a <c>declaration_keys</c> member naming the header's key. A reader that refused every
    /// declaration would pass HSU19 to HSU23 and fail this. Red on the seam: the vectors are <c>/1</c> under a log key.
    /// </summary>
    [Fact]
    public void R6_62_HSU24_ADeclarationUnderTheFirstDeclarationKeyDisputesThePost()
    {
        const string Name = "compromised-before-post";
        var dir = Path.Combine(LifecycleConformanceTests.Root, Name);
        var statements = Directory.EnumerateFiles(Path.Combine(dir, "entries"), "*.json")
            .Select(f => JsonDocument.Parse(File.ReadAllBytes(f)))
            .Where(d => d.RootElement.GetProperty("entry").GetProperty("event_type").GetString() == "agent.key-compromised")
            .Select(d => d.RootElement.GetProperty("entry").GetProperty("payload").GetProperty("statement").GetString()!)
            .ToList();
        using var keys = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "log-jwks.json")));

        var typ = statements.Count == 1 && statements[0].Contains("\"typ\":\"curia.key-compromise/2\"", StringComparison.Ordinal) ? "/2" : "not /2";
        var served = keys.RootElement.TryGetProperty("declaration_keys", out var declarationKeys) && declarationKeys.GetArrayLength() > 0 ? "served" : "not served";

        AllHold(
            Describe(Name, "disputed", "disputed"),
            typ == "/2" ? string.Empty : $"{Name}: the case's declaration statement is {typ}",
            served == "served" ? string.Empty : $"{Name}: the case's key set has declaration_keys {served}");
    }

    /// <summary>HSU25. The pair that carries the difference between an absence and a denial: no <c>declaration_keys</c> member is could not be checked; a served empty one is failed.</summary>
    [Fact]
    public void R6_62_HSU25_AnAbsentKeySetMemberIsNotADenialAndAnEmptyOneIs() =>
        AllHold(
            Describe("compromise-declaration-key-set-absent", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-declaration-keys-empty", "failed", "declaration-key-unpublished"));

    /// <summary>
    /// HSU44 (this reader's half; Amendment 3 rulings 38 and 39). The six vectors that state the readers' undeclared decisions, each by hand: a <c>declaration_keys</c> that is not an array
    /// is read as absent (a <c>null</c> and an object, two JSON types); a position not given is decided before a log key; a log key is a log key by public key under another kid; an unpositioned
    /// other declaration key makes "first" undecidable; a kid listed twice is the key of its earliest listing.
    /// </summary>
    [Fact]
    public void R6_62_HSU44_TheReadersUndeclaredDecisionsHoldInTheSixVectors() =>
        AllHold(
            Describe("compromise-declaration-keys-null", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-declaration-keys-object", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-log-key-kid-position-unknown", "could-not-check", "declaration-key-position-unknown"),
            Describe("compromise-declaration-key-is-log-key-under-another-kid", "failed", "declaration-key-is-log-key"),
            Describe("compromise-another-declaration-key-position-unknown", "could-not-check", "declaration-key-position-unknown"),
            Describe("compromise-declaration-kid-published-twice", "failed", "declaration-signature-invalid"));

    /// <summary>
    /// HSU49 (this reader's half; Amendment 4 rulings 46 to 49). The six vectors that state what both readers decide about an entry no reader can read, a kid listed twice with one
    /// listing unpositioned, and two keys at one position, each by hand: an unreadable entry, in either member, leaves the key set without a usable member; a kid with an
    /// unpositioned listing has no earliest; of two keys at one position neither is the first.
    /// </summary>
    [Fact]
    public void R6_62_HSU49_TheUnreadableEntryAndTheRepeatedKidAndTheTieHoldInTheSixVectors() =>
        AllHold(
            Describe("compromise-declaration-kid-twice-one-unpositioned", "could-not-check", "declaration-key-position-unknown"),
            Describe("compromise-declaration-key-without-kid", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-log-key-without-kid", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-declaration-keys-share-position", "failed", "declaration-key-not-first"),
            Describe("compromise-declaration-keys-entry-not-an-object", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-declaration-keys-entry-without-alg", "could-not-check", "declaration-key-set-not-supplied"));

    private const string TwoKeysCase = "compromise-first-of-two-declaration-keys";

    private static readonly string[] Members = ["keys", "declaration_keys"];

    /// <summary>
    /// The key set of <c>compromise-first-of-two-declaration-keys</c> with <paramref name="appended"/> appended to <paramref name="member"/>: a copy of the second declaration key's JWK
    /// (for <c>keys</c>, with a kid of its own; for <c>declaration_keys</c>, at <c>log_index</c> 9), then whatever the row does to it.
    /// </summary>
    private static byte[] KeySetWith(string member, Func<JsonObject, IEnumerable<JsonNode?>> appended)
    {
        var set = JsonNode.Parse(File.ReadAllBytes(Path.Combine(LifecycleConformanceTests.Root, TwoKeysCase, "log-jwks.json")))!.AsObject();
        var second = set["declaration_keys"]!.AsArray().Select(k => k!.AsObject()).Single(k => k["log_index"]!.GetValue<long>() == 4);
        var copy = (JsonObject)second.DeepClone();
        copy["log_index"] = 9;
        if (member == "keys") copy["kid"] = "conformance-extra-log-key";
        var array = set[member]!.AsArray();
        foreach (var item in appended(copy)) array.Add(item);
        return JsonSerializer.SerializeToUtf8Bytes(set);
    }

    private static string Verdict(byte[] logJwks)
    {
        var (verdict, predicate) = LifecycleConformanceTests.Judge(LifecycleConformanceTests.Load(TwoKeysCase, logJwks));
        return $"{verdict} {predicate}";
    }

    /// <summary>
    /// HSU50, table 1 (asserted; Amendment 4 ruling 47 and 48). To the member under test (<c>keys</c>, then <c>declaration_keys</c>) a copy of the second declaration key's JWK is appended
    /// and then mutated: <c>kid</c>, <c>kty</c>, <c>alg</c> or <c>x</c> dropped, or the entry replaced by <c>7</c> or by <c>null</c>. Each of the twelve is an entry no reader can read as a key,
    /// and each is could not be checked, <c>declaration-key-set-not-supplied</c>; the control, the copy unmutated, is disputed in each member (so that a reader refusing every longer key set is red).
    /// </summary>
    [Fact]
    public void R6_62_HSU50_AnEntryNoReaderCanReadLeavesTheKeySetWithoutAUsableMemberInEitherMember()
    {
        static JsonNode? Dropping(JsonObject copy, string member)
        {
            copy.Remove(member);
            return copy;
        }

        var rows = new List<(string Row, Func<JsonObject, JsonNode?> Mutate)>
        {
            ("drop kid", c => Dropping(c, "kid")),
            ("drop kty", c => Dropping(c, "kty")),
            ("drop alg", c => Dropping(c, "alg")),
            ("drop x", c => Dropping(c, "x")),
            ("replace by 7", _ => JsonNode.Parse("7")),
            ("replace by null", _ => null),
        };

        var problems = new List<string>();
        foreach (var member in Members)
        {
            var control = Verdict(KeySetWith(member, c => [c]));
            if (control != "disputed disputed") problems.Add($"{member} control: expected disputed disputed, got {control}");

            foreach (var (row, mutate) in rows)
            {
                var got = Verdict(KeySetWith(member, c => [mutate(c)]));
                if (got != "could-not-check declaration-key-set-not-supplied") problems.Add($"{member} {row}: expected could-not-check declaration-key-set-not-supplied, got {got}");
            }
        }

        AllHold([.. problems]);
    }

    /// <summary>
    /// HSU52 (this reader's half; Amendment 6 rulings 58 to 60). The six vectors, by hand from Amendment 6's table: a declaring key whose point is not on its curve fails at the signature; another
    /// declaration key or a log key under another kid whose point is not on its curve is a key and is not the declaring key's, so the declaration disputes; a log key under the declaration key's kid
    /// is a log key, and so is one that states the declaring key's points; one key listed twice at one position under other members is one key and keeps its position. One file for both readers
    /// (<c>tests/declaration_keys.rs</c> holds the same table); a row measured in two runners is a vector or it is two measurements.
    /// </summary>
    [Fact]
    public void R6_62_HSU52_AnEntryOfTheClosedFormThatIsNotAPointOnTheCurveHoldsInTheSixVectors() =>
        AllHold(
            Describe("compromise-declaration-key-off-curve", "failed", "declaration-signature-invalid"),
            Describe("compromise-other-declaration-key-off-curve", "disputed", "disputed"),
            Describe("compromise-log-key-off-curve", "disputed", "disputed"),
            Describe("compromise-log-key-off-curve-under-declaration-kid", "failed", "declaration-key-is-log-key"),
            Describe("compromise-declaration-key-and-log-key-off-curve", "failed", "declaration-key-is-log-key"),
            Describe("compromise-declaration-kid-twice-at-one-position-same-key", "disputed", "disputed"));

    /// <summary>
    /// HSU51 (this reader's half; Amendment 5 rulings 52 and 53). The four vectors, by hand: an entry that carries the four strings and is no usable key (no <c>y</c>; an <c>x</c> that is not
    /// base64url; <c>kty</c> RSA), in either member, leaves the key set without a usable member; a kid twice at one position with keys that differ has no earliest listing.
    /// </summary>
    [Fact]
    public void R6_62_HSU51_AnEntryOutsideTheClosedFormAndAKidTwiceAtOnePositionHoldInTheFourVectors() =>
        AllHold(
            Describe("compromise-declaration-key-without-y", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-declaration-key-x-not-base64url", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-log-key-kty-rsa", "could-not-check", "declaration-key-set-not-supplied"),
            Describe("compromise-declaration-kid-twice-at-one-position", "could-not-check", "declaration-key-position-unknown"));

    /// <summary>
    /// HSU51's table (asserted; Amendment 5 ruling 52). To the member under test a copy of the second declaration key's JWK is appended and then mutated into an entry outside R6.62's closed form
    /// that still carries a string <c>kid</c>, <c>kty</c>, <c>alg</c> and <c>x</c> or is one step from it: no <c>y</c>; an <c>x</c> that begins with an asterisk; <c>kty</c> RSA; a <c>kid</c> that is a number;
    /// an <c>x</c> one character short; an <c>x</c> whose last character carries non-zero trailing bits; <c>alg</c> <c>EdDSA</c> over <c>kty</c> <c>EC</c>; no <c>crv</c>. Eight rows in each of two members,
    /// each could not be checked, <c>declaration-key-set-not-supplied</c>; the control, the copy unmutated, is disputed in each member (HSU50's table 1).
    /// </summary>
    [Fact]
    public void R6_62_HSU51_AnEntryOutsideTheClosedFormLeavesTheKeySetWithoutAUsableMemberInEitherMember()
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var rows = new List<(string Row, Func<JsonObject, JsonNode?> Mutate)>
        {
            ("drop y", c => { c.Remove("y"); return c; }),
            ("x begins with an asterisk", c => { c["x"] = "*" + c["x"]!.GetValue<string>()[1..]; return c; }),
            ("kty RSA", c => { c["kty"] = "RSA"; return c; }),
            ("kid a number", c => { c["kid"] = 7; return c; }),
            ("x one character short", c => { var x = c["x"]!.GetValue<string>(); c["x"] = x[..^1]; return c; }),
            ("x with non-zero trailing bits", c =>
            {
                var x = c["x"]!.GetValue<string>();
                c["x"] = x[..^1] + Alphabet[(Alphabet.IndexOf(x[^1], StringComparison.Ordinal) + 1) % 64];
                return c;
            }),
            ("alg EdDSA over kty EC", c => { c["alg"] = "EdDSA"; return c; }),
            ("drop crv", c => { c.Remove("crv"); return c; }),
        };

        var problems = new List<string>();
        foreach (var member in Members)
        {
            foreach (var (row, mutate) in rows)
            {
                var got = Verdict(KeySetWith(member, c => [mutate(c)]));
                if (got != "could-not-check declaration-key-set-not-supplied") problems.Add($"{member} {row}: expected could-not-check declaration-key-set-not-supplied, got {got}");
            }
        }

        AllHold([.. problems]);
    }
}
