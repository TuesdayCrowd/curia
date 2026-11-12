using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Application.Credentials;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.LifecycleProbeSupport;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// LRV2 and LRV23 (errata G68, R6.64): the verifier agrees with the 81 reader vectors in <c>conformance/lifecycle/</c> wherever a reader's verdict names a row's predicate, and R6.64's
/// predicate names are the vectors' names. The table of which predicates are row-level is a literal here, checked against R6.64's own text; the vectors' verdicts are another artifact from the verifier.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReaderVectorAgreementTests
{
    /// <summary>The predicates of a failed reader verdict that name a row: R6.64's ten (the quote of its list, by name).</summary>
    private static readonly string[] RowLevel =
    [
        "statement-mismatch", "broken-link", "lifecycle-typ-mismatch", "rotation-unauthorized", "declaration-key-unpublished",
        "declaration-key-is-log-key", "declaration-key-not-first", "declaration-signature-invalid", "kid-not-bound-to-agent", "declaration-mismatch",
    ];

    /// <summary>The two a reader reports as could-not-be-checked and the Forum skips: a kind or a reason this build does not know.</summary>
    private static readonly string[] UnknownKind = ["unknown-lifecycle-kind", "unknown-reason"];

    /// <summary>The predicates that are about the post or the document and not about a row; the cases that carry one are listed and held to be none of the above.</summary>
    private static readonly string[] About =
    [
        "retired-before-post", "compromised-before-post", "bound-after-post", "post-entry-mismatch", "lifecycle-not-supplied", "lifecycle-too-long",
        "inclusion-invalid", "declaration-key-set-not-supplied", "declaration-key-position-unknown",
    ];

    /// <summary>
    /// The log a reader vector describes, as the Forum would hold it: the entries at their indices, a filler where the vector holds none, and the keys <c>log-jwks.json</c> publishes as
    /// <c>log.key</c> and <c>log.declaration-key</c> entries, each at its <c>log_index</c> when that slot is free and otherwise above the highest index of any entry and of any key's <c>log_index</c> (the same placement for every case).
    /// </summary>
    internal static IReadOnlyList<AppendedEvent> LogOf(ConformanceLog log)
    {
        var slots = new SortedDictionary<long, AppendedEvent>();
        for (var i = 0; i < 4096; i++)
        {
            var file = Path.Combine(log.Directory, "entries", i.ToString(CultureInfo.InvariantCulture) + ".json");
            if (File.Exists(file)) slots[i] = ConformanceLog.Event(i, File.ReadAllBytes(file));
        }

        using var jwks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(log.Directory, "log-jwks.json")));
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(jwks.RootElement.GetRawText()), AdmitLimits.Default);
        var published = new PublishedKeys(parsed.Match(v => v, e => throw new InvalidOperationException(e.Type)));
        var keys = published.LogKeys.Select(k => (Type: LogEntries.KeyType, k.LogIndex, k.Kid, k.Jwk, k.ValidFrom))
            .Concat(published.DeclarationKeys.Select(k => (Type: LogEntries.DeclarationKeyType, k.LogIndex, k.Kid, k.Jwk, k.ValidFrom)))
            .ToList();

        // One more than the highest index of any entry and of any key's log_index (ruling 54): a key that cannot take its own position goes after everything, in served order, and no later key's own slot is taken.
        var tail = Math.Max(slots.Keys.Count == 0 ? -1 : slots.Keys.Max(), keys.Count == 0 ? -1 : keys.Max(k => k.LogIndex)) + 1;
        foreach (var key in keys)
        {
            var at = slots.ContainsKey(key.LogIndex) ? tail++ : key.LogIndex;
            slots[at] = KeyEntry(at, key.Type, key.Kid, key.Jwk, key.ValidFrom);
        }

        var events = new List<AppendedEvent>();
        for (long i = 0; i <= (slots.Count == 0 ? -1 : slots.Keys.Max()); i++)
            events.Add(slots.TryGetValue(i, out var held) ? held : Filler(i));
        return events;
    }

    private static AppendedEvent KeyEntry(long index, string type, string kid, JsonValue.Object jwk, string validFrom) =>
        LogEvents.At(
            index,
            type == LogEntries.KeyType ? LogEntries.KeysAggregate : LogEntries.DeclarationKeysAggregate,
            DateTimeOffset.Parse(validFrom, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind),
            $"synthetic-key-{index.ToString(CultureInfo.InvariantCulture)}",
            type,
            "operator:synthetic",
            new JsonValue.Object([new("kid", S(kid)), new("jwk", jwk), new("valid_from", S(validFrom))]));

    private static AppendedEvent Filler(long index) =>
        LogEvents.At(index, $"filler-{index.ToString(CultureInfo.InvariantCulture)}", DateTimeOffset.UnixEpoch, $"synthetic-filler-{index.ToString(CultureInfo.InvariantCulture)}", "synthetic.filler", "operator:synthetic", new JsonValue.Object([]));

    /// <summary>The three vectors not asked (ruling 54), by name; each is asserted to be a directory of <c>conformance/lifecycle/</c>, since an exclusion of a name that does not exist excludes nothing.</summary>
    private static readonly string[] NotAsked = ["foreign-identity-entry", "unknown-lifecycle-kind", "compromise-declaration-keys-share-position"];

    [Fact]
    public void LRV2_TheVerifierAgreesWithTheReaderVectors()
    {
        var failures = new List<string>();
        var classified = 0;
        var verifier = ConformanceLog.Verifier();
        foreach (var excluded in NotAsked)
            Assert.Contains(excluded, ConformanceLog.Names("lifecycle"));
        foreach (var name in ConformanceLog.Names("lifecycle"))
        {
            var log = ConformanceLog.Open("lifecycle", name);
            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(log.Directory, "expected.json")));
            var verdict = expected.RootElement.GetProperty("verdict").GetString()!;
            var predicate = expected.RootElement.TryGetProperty("predicate", out var p) ? p.GetString()! : string.Empty;

            // The two cases with no row of the stream to decide are not asked (a foreign identity's entry; a kind no stream holds), as the Rust fold excludes them by name.
            // A third is a key set that no log can be: two declaration keys at one position. R6.64 reads "the log's own entries as the key set ... so that there is neither a key set that is not
            // supplied nor a position that is not given", and a log gives each entry a position of its own, so the nearest log (the second key after the last entry, the first key still first)
            // decides the row honoured and the reader's verdict, which the document's shape alone produces, has no log to agree with.
            if (NotAsked.Contains(name)) { classified++; continue; }

            var verified = verifier.OfLog(LogOf(log)).For(Typed.Agent(log.Agent));
            var skipped = verified.Refusal is null ? verified.Skipped : [];
            var described = verified.Refusal is { } refusal ? $"Refusal {refusal.Type}" : $"{(skipped.Length == 0 ? "no row skipped" : string.Join(", ", skipped.Select(r => $"{r.LogIndex} {r.Predicate}")))}";

            if (verdict == "failed" && RowLevel.Contains(predicate, StringComparer.Ordinal))
            {
                classified++;
                if (skipped.Length == 0 || skipped[0].Predicate != predicate)
                    failures.Add($"{name}: Expected: skipped {predicate} at the row  Actual: {described}");
            }
            else if (verdict == "could-not-check" && UnknownKind.Contains(predicate, StringComparer.Ordinal))
            {
                classified++;
                if (!skipped.Any(r => r.Predicate == predicate))
                    failures.Add($"{name}: Expected: a row skipped {predicate}  Actual: {described}");
            }
            else if (verdict is "verified" or "disputed")
            {
                classified++;
                if (verified.Refusal is not null || skipped.Length > 0)
                    failures.Add($"{name}: Expected: no row skipped  Actual: {described}");
            }
            else if (About.Contains(predicate, StringComparer.Ordinal))
            {
                classified++;
                if (RowLevel.Contains(predicate, StringComparer.Ordinal) || UnknownKind.Contains(predicate, StringComparer.Ordinal))
                    failures.Add($"{name}: {predicate} is listed as about the post and is a row predicate");
            }
        }

        Assert.True(classified == ConformanceLog.Names("lifecycle").Length, $"every reader vector falls in one category of the table: {classified} of {ConformanceLog.Names("lifecycle").Length} do");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count} cases differ. The first: {failures.FirstOrDefault()}{Environment.NewLine}All:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>The literal tables above are quotations of R6.64: every row-level predicate and the two unknown-kind ones are names the paragraph gives.</summary>
    [Fact]
    public void LRV2_TheRowLevelTableIsQuotedFromR664()
    {
        var published = R664Text.Slugs();

        foreach (var name in RowLevel.Concat(UnknownKind))
            Assert.Contains(name, published);
        foreach (var name in About)
            Assert.DoesNotContain(name, published);
    }

    /// <summary>LRV23: the predicate names R6.64 gives are the predicate names the 44 vectors carry, plus at most two the text names and no vector uses (a literal list, with its reason).</summary>
    [Fact]
    public void LRV23_R664sSlugsAreTheHonouredVectorsSlugs()
    {
        // At most two: the names R6.64 gives that no honoured vector uses. None today (every one of the twelve is used).
        string[] namedAndUnused = [];
        Assert.True(namedAndUnused.Length <= 2);

        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in ConformanceLog.Names("honoured"))
        {
            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ConformanceLog.Root(), "honoured", name, "expected.json")));
            var blocks = new List<JsonElement> { expected.RootElement };
            if (expected.RootElement.TryGetProperty("also", out var also)) blocks.AddRange(also.EnumerateArray());
            foreach (var row in blocks.SelectMany(b => b.GetProperty("rows").EnumerateArray()))
                if (row.TryGetProperty("predicate", out var predicate)) used.Add(predicate.GetString()!);
        }

        var published = R664Text.Slugs();
        Assert.Equal(
            "none: named by the text and used by no vector; none: used by a vector and named by no text",
            $"{Show(published.Except(used).Except(namedAndUnused))}: named by the text and used by no vector; {Show(used.Except(published))}: used by a vector and named by no text");
    }

    private static string Show(IEnumerable<string> names) => names.Any() ? string.Join(", ", names) : "none";
}
