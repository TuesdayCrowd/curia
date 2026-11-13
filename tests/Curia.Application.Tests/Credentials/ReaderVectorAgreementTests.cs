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
/// LRV2 and LRV23 (errata G68, R6.64): the verifier agrees with the 84 reader vectors (80 asked: 56 compared, 18 about the post or the document, 6 whose reader verdict is could-not-check by a kind or a reason the Forum names too; 4 not asked) in <c>conformance/lifecycle/</c> wherever a reader's verdict names a row's predicate, and R6.64's
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
    ///
    /// <para>A key whose stated index an entry on disk holds is therefore displaced (Amendment 8, ruling 94). This runner cannot do otherwise: the verifier takes an entry's position from its place in the list
    /// (R6.47), so two events cannot be handed to it at one index. The Rust runner's log is a set of indexed entries and keeps such a key at its stated index, which is the placement a reader is handed.
    /// The two logs of a vector then differ only in where those keys sit, and <see cref="LRV2_NoKeyThatDecidesARowSitsWhereAnEntryIs"/> pins that, in every vector compared, the head log key is the only
    /// one and is no declaration key: a key that decides no row, wherever it sits.</para>
    /// </summary>
    internal static IReadOnlyList<AppendedEvent> LogOf(ConformanceLog log) => Place(log).Log;

    /// <summary><see cref="LogOf"/>'s log, and the keys it displaced because an entry on disk (not another key) held the index their key set states: the keys the two runners place differently.</summary>
    private static (IReadOnlyList<AppendedEvent> Log, IReadOnlyList<(string Kid, long LogIndex)> DisplacedByAnEntry) Place(ConformanceLog log)
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
        var onDisk = slots.Keys.ToHashSet();
        var displacedByAnEntry = new List<(string Kid, long LogIndex)>();
        foreach (var key in keys)
        {
            if (onDisk.Contains(key.LogIndex)) displacedByAnEntry.Add((key.Kid, key.LogIndex));
            var at = slots.ContainsKey(key.LogIndex) ? tail++ : key.LogIndex;
            slots[at] = KeyEntry(at, key.Type, key.Kid, key.Jwk, key.ValidFrom);
        }

        var events = new List<AppendedEvent>();
        for (long i = 0; i <= (slots.Count == 0 ? -1 : slots.Keys.Max()); i++)
            events.Add(slots.TryGetValue(i, out var held) ? held : Filler(i));
        return (events, displacedByAnEntry);
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

    /// <summary>The four vectors not asked (ruling 54; the fourth, <c>compromise-log-key-position-not-given</c>, is a key set no log can be: a log gives every entry a position, and a reader fails a log key with none closed, Amendment 7 ruling 82), by name; each is asserted to be a directory of <c>conformance/lifecycle/</c>, since an exclusion of a name that does not exist excludes nothing.</summary>
    private static readonly string[] NotAsked = ["foreign-identity-entry", "unknown-lifecycle-kind", "compromise-declaration-keys-share-position", "compromise-log-key-position-not-given"];

    [Fact]
    public void LRV2_TheVerifierAgreesWithTheReaderVectors()
    {
        var failures = new List<string>();
        var classified = 0;
        var compared = 0;
        var about = 0;
        var couldNotCheck = 0;
        var notAsked = 0;
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
            // A third (and a fourth, a log key that gives no position) is a key set that no log can be: two declaration keys at one position. R6.64 reads "the log's own entries as the key set ... so that there is neither a key set that is not
            // supplied nor a position that is not given", and a log gives each entry a position of its own, so the nearest log (the second key after the last entry, the first key still first)
            // decides the row honoured and the reader's verdict, which the document's shape alone produces, has no log to agree with.
            if (NotAsked.Contains(name)) { classified++; notAsked++; continue; }

            var verified = verifier.OfLog(LogOf(log)).For(Typed.Agent(log.Agent));
            var skipped = verified.Skipped;
            var described = skipped.Length == 0 ? "no row skipped" : string.Join(", ", skipped.Select(r => $"{r.LogIndex} {r.Predicate}"));

            if (verdict == "failed" && RowLevel.Contains(predicate, StringComparer.Ordinal))
            {
                classified++;
                compared++;
                if (skipped.Length == 0 || skipped[0].Predicate != predicate)
                    failures.Add($"{name}: Expected: skipped {predicate} at the row  Actual: {described}");
            }
            else if (verdict == "could-not-check" && UnknownKind.Contains(predicate, StringComparer.Ordinal))
            {
                classified++;
                couldNotCheck++;
                if (!skipped.Any(r => r.Predicate == predicate))
                    failures.Add($"{name}: Expected: a row skipped {predicate}  Actual: {described}");
            }
            else if (verdict is "verified" or "disputed")
            {
                classified++;
                compared++;
                if (skipped.Length > 0)
                    failures.Add($"{name}: Expected: no row skipped  Actual: {described}");
            }
            else if (About.Contains(predicate, StringComparer.Ordinal))
            {
                classified++;
                about++;
                if (RowLevel.Contains(predicate, StringComparer.Ordinal) || UnknownKind.Contains(predicate, StringComparer.Ordinal))
                    failures.Add($"{name}: {predicate} is listed as about the post and is a row predicate");
            }
        }

        // Three exact numbers (Amendment 7, ruling 88): a vector that stops being compared, or moves to About, changes one of them. They are counted from conformance/lifecycle/: the vectors the verifier is asked (a row-level
        // verdict, an unknown kind, or a verdict of verified or disputed), the vectors about the post or the document and not a row, and the vectors not asked.
        Assert.Equal("compared 56; about 18; could not check 6; not asked 4", $"compared {compared}; about {about}; could not check {couldNotCheck}; not asked {notAsked}");
        Assert.True(classified == ConformanceLog.Names("lifecycle").Length, $"every reader vector falls in one category of the table: {classified} of {ConformanceLog.Names("lifecycle").Length} do");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count} cases differ. The first: {failures.FirstOrDefault()}{Environment.NewLine}All:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>The <c>kid</c> every reader vector gives its head log key, which each lists at index 0, where its enrolment is.</summary>
    private const string HeadLogKey = "conformance-log-ed25519";

    /// <summary>
    /// Amendment 8, ruling 94: this runner moves a key whose stated index an entry holds above every entry, and the Rust runner leaves it where it is stated. In every vector either runner compares, the
    /// only such key is the head log key, and in all 84 no declaration key is the head log key by <c>kid</c> or by public key. So the two runners' logs differ only in where a key that decides no row sits.
    /// </summary>
    [Fact]
    public void LRV2_NoKeyThatDecidesARowSitsWhereAnEntryIs()
    {
        var failures = new List<string>();
        var compared = 0;
        var headKeyDisplaced = 0;
        foreach (var name in ConformanceLog.Names("lifecycle"))
        {
            var log = ConformanceLog.Open("lifecycle", name);

            // Over all 84, asked or not: no declaration key carries the head log key's kid or its x.
            using var jwks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(log.Directory, "log-jwks.json")));
            var headX = Members(jwks.RootElement, "keys").Where(k => Text(k, "kid") == HeadLogKey).Select(k => Text(k, "x")).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
            foreach (var key in Members(jwks.RootElement, "declaration_keys"))
            {
                if (Text(key, "kid") == HeadLogKey || (Text(key, "x") is { } x && headX.Contains(x)))
                    failures.Add($"{name}: a declaration key carries the head log key's kid or public key ({HeadLogKey}): the two runners place the head key differently, so it may decide no row");
            }

            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(log.Directory, "expected.json")));
            var verdict = expected.RootElement.GetProperty("verdict").GetString()!;
            var predicate = expected.RootElement.TryGetProperty("predicate", out var p) ? p.GetString()! : string.Empty;
            if (NotAsked.Contains(name) || !Compares(verdict, predicate)) continue;

            compared++;
            foreach (var (kid, index) in Place(log).DisplacedByAnEntry)
            {
                if (kid == HeadLogKey)
                    headKeyDisplaced++;
                else
                    failures.Add($"{name}: key {kid} is stated at index {index.ToString(CultureInfo.InvariantCulture)}, which an entry holds; state the key at a free index (a stand-in entry, as compromise-declaration-key-and-log-key-off-curve does): the C# runner cannot hold a key at an index an entry holds");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} keys differ in placement between the runners and may decide a row. The first: {failures.FirstOrDefault()}{Environment.NewLine}All:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");

        // The pin looked at something: the 62 vectors LRV2 compares (56 and 6), and in each the head key, displaced once.
        Assert.Equal("compared 62; the head key displaced in 62", $"compared {compared}; the head key displaced in {headKeyDisplaced}");
    }

    /// <summary>The classes of <see cref="LRV2_TheVerifierAgreesWithTheReaderVectors"/> that assert on the fold's rows: a row-level failure, a verdict of verified or disputed, and an unknown kind or reason.</summary>
    private static bool Compares(string verdict, string predicate) =>
        (verdict == "failed" && RowLevel.Contains(predicate, StringComparer.Ordinal))
        || (verdict == "could-not-check" && UnknownKind.Contains(predicate, StringComparer.Ordinal))
        || verdict is "verified" or "disputed";

    private static JsonElement[] Members(JsonElement keySet, string member) =>
        keySet.ValueKind == JsonValueKind.Object && keySet.TryGetProperty(member, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [];

    private static string? Text(JsonElement key, string member) =>
        key.ValueKind == JsonValueKind.Object && key.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
        // The names R6.64 gives that no honoured vector uses: none today (every one of the twelve is used), and the comparison below states it.
        string[] namedAndUnused = [];

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
