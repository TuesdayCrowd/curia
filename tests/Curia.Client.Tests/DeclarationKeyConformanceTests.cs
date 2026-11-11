using System.Diagnostics.CodeAnalysis;
using System.Text;
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
public sealed class DeclarationKeyConformanceTests(Xunit.ITestOutputHelper output)
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
    /// HSU18 (this reader's half). The family holds the 71 lifecycle cases (Amendment 1 ruling 26's 58, the control of HSU22, the six of HSU44 and the six of HSU49) and the 12 acta cases, and the index declares
    /// what the directories hold. Red on the seam: <c>lifecycle 53</c>. (Every case reaching its own expected verdict is the existing <c>R6_54_EveryLifecycleCaseReachesItsVerdictAndNamesItsPredicate</c>.)
    /// </summary>
    [Fact]
    public void R6_62_HSU18_TheFamiliesHoldTheCountsOfTheDeclarationKeyVectors()
    {
        Assert.Equal(
            "lifecycle 71 (index 71); acta 12 (index 12)",
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
    /// HSU50, table 2 (measured, not asserted; Amendment 4 ruling 50): entries readable by the four-member definition whose material a reader may refuse, in each member, read by this
    /// reader and written to the test output beside the Rust reader's own table (<c>tests/lifecycle.rs</c>). A row on which the two differ is a stop, filed as D61 item 268.
    /// </summary>
    [Fact]
    public void R6_62_HSU50_TheMaterialAReaderMayRefuseIsMeasured()
    {
        var rows = new List<(string Row, Func<JsonObject, IEnumerable<JsonNode?>> Append)>
        {
            ("an EC key without y", c => { c.Remove("y"); return [c]; }),
            ("an x that does not decode", c => { c["x"] = "!!!"; return [c]; }),
            ("a kty of RSA", c => { c["kty"] = "RSA"; return [c]; }),
            ("a kid that is a number", c => { c["kid"] = 7; return [c]; }),
            ("one kid twice at one position with other key material", c =>
            {
                var other = (JsonObject)c.DeepClone();
                other["x"] = new string('A', 43);
                return [c, other];
            }),
        };

        var table = new StringBuilder("HSU50 table 2 (C#):");
        var measured = 0;
        foreach (var member in Members)
        {
            foreach (var (row, append) in rows)
            {
                string got;
                try
                {
                    got = Verdict(KeySetWith(member, append));
                }
                catch (Xunit.Sdk.XunitException refused)
                {
                    got = "refused: " + refused.Message.Split('\n')[0];
                }

                table.Append('\n').Append(Line(member, row, got));
                measured++;
            }
        }

        output.WriteLine(table.ToString());
        Assert.Equal(10, measured);
    }

    private static string Line(string member, string row, string got) => $"  {member,-17} | {row,-55} | {got}";
}
