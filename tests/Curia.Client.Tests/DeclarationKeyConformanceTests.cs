using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
    /// HSU18 (this reader's half). The family holds the 59 lifecycle cases (Amendment 1 ruling 26's 58 and the control of HSU22) and the 12 acta cases, and the index declares
    /// what the directories hold. Red on the seam: <c>lifecycle 53</c>. (Every case reaching its own expected verdict is the existing <c>R6_54_EveryLifecycleCaseReachesItsVerdictAndNamesItsPredicate</c>.)
    /// </summary>
    [Fact]
    public void R6_62_HSU18_TheFamiliesHoldTheCountsOfTheDeclarationKeyVectors()
    {
        Assert.Equal(
            "lifecycle 59 (index 59); acta 12 (index 12)",
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
}
