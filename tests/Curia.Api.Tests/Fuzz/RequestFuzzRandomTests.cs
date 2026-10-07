using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using CsCheck;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R14.10's CsCheck pass (spec §4.10): 2,000 draws from a fixed seed, each a uniformly drawn row, a
/// part drawn uniformly from that row's derived parts, and a hostile string put there. The draws are
/// taken in a plain loop over <see cref="PCG"/> and each generator's own <c>Generate</c>, never through
/// <c>Check.Sample*</c>, so CsCheck's shrinker never replays a draw against a server whose state the
/// failing draw changed, and none of its <c>CsCheck_*</c> environment variables is read. The pass stops
/// at its first failure and prints the seed and the row.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("fuzz")]
public sealed class RequestFuzzRandomTests(FuzzForumFixture forum) : IClassFixture<FuzzForumFixture>
{
    /// <summary>
    /// <c>new CsCheck.PCG(1, 20261006).ToString()</c> under CsCheck 4.8.0: stream 1, seed 20261006 (the
    /// stage's date), printed once in a scratch console and pasted here. Fixed: the pass is replayable.
    /// </summary>
    internal const string Seed = "0000001diyh1";

    /// <summary>The number of draws, one thread.</summary>
    internal const int Draws = 2_000;

    /// <summary>
    /// The floor on JSON and JWS draws past the first parser: at least one in <c>Divisor</c>. Measured
    /// with every ADMIT refusal and the token endpoint's proof refusals counted as stopped there, and
    /// noncharacters drawn only in the decoding-hostile strings (review of 6cbfa9f): 643 of 1,217, a
    /// fraction of 0.528; half of it is 0.264, and 4 is the smallest integer whose reciprocal is at
    /// most that. The figure first recorded here, 643 of 1,151 (0.559), counted 149 ADMIT refusals
    /// (139 curia/admit/noncharacter) as past the parser (review of 6cbfa9f). The per-unit generator
    /// before both measured 87 of 1,100 (0.079) with the same seed.
    /// </summary>
    internal const int Divisor = 4;

    /// <summary>
    /// The floor on POST /v1/posts <c>json:/envelope/*</c> draws past ADMIT, the route the pass exists
    /// to combine on: at least one in <c>EnvelopeDivisor</c>. Measured in the same run as
    /// <see cref="Divisor"/> (review of 6cbfa9f): 103 of 132, a fraction of 0.780; half of it is 0.390,
    /// and 3 is the smallest integer whose reciprocal is at most that.
    /// </summary>
    internal const int EnvelopeDivisor = 3;

    /// <summary>
    /// The only pass that combines hostile parts: one draw can land in a JWS whose claims are already
    /// hostile, or beside a header another part's value made strange. The closed pass varies one part
    /// at a time against a well-formed exemplar, so nothing else here sends two hostile parts at once.
    /// </summary>
    [Fact]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A draw no request can be built for is counted unsent; any exception from the send is a failure carrying its type (spec §4.10).")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The request is disposed by the using block that sends it; building it either returns it or throws.")]
    public async Task R14_10_NoRandomStringInAnyPartIsAServerFault()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = await FuzzContext.SeedAsync(forum, ct);

        // A row with no part (GET /health and the other anonymous reads with no parameter) has nothing to draw.
        var rows = new List<ExemplarRow>();
        foreach (var candidate in Exemplars.All)
        {
            context.Prepared = null;
            if ((await candidate.Build(context, ct)).Parts().Count > 0) rows.Add(candidate);
        }

        var pcg = PCG.Parse(Seed);
        var rowGen = Gen.Int[0, rows.Count - 1];
        var warmed = new HashSet<string>(StringComparer.Ordinal);
        var slowest = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var statuses = new SortedDictionary<int, int>();
        var sent = 0;
        var unsent = 0;
        var jsonOrJws = 0;
        var pastParser = 0;
        var envelopeDraws = 0;
        var envelopePast = 0;
        var matchedRandom = new HashSet<FaultRow>();
        int? stoppedAt = null;
        string? failure = null;

        for (var draw = 0; draw < Draws && failure is null; draw++)
        {
            var row = rows[rowGen.Generate(pcg, null, out _)];
            context.Prepared = null;
            if (row.Prepare is not null)
            {
                forum.Clock.Advance(row.PrepareStep);
                await row.Prepare(context, ct);
            }

            if (row.CreatesPosts) forum.Clock.Advance(TimeSpan.FromHours(1));

            var model = await row.Build(context, ct);
            var parts = model.Parts();
            var part = parts[Gen.Int[0, parts.Count - 1].Generate(pcg, null, out _)];
            var text = HostileText.Generator.Generate(pcg, null, out _);
            var value = text.At(part);
            var copy = model.IsSigned(part) ? CopyKind.ReSigned : CopyKind.Plain;

            HttpRequestMessage request;
            try
            {
                try
                {
                    request = model.Render(part, value, copy);
                }
                catch (NotReSignableException)
                {
                    // No canonical form, so no re-signed copy: the unre-signed copy is sent (spec §4.10).
                    copy = CopyKind.Unsigned;
                    request = model.Render(part, value, copy);
                }
            }
            catch (Exception)
            {
                unsent++;
                continue;
            }

            var warmUp = warmed.Add(row.Route);
            var stopwatch = Stopwatch.StartNew();
            int status;
            string body;
            string? thrown = null;
            using (request)
            {
                try
                {
                    using var response = await context.Client!.SendAsync(request, ct);
                    body = await response.Content.ReadAsStringAsync(ct);
                    status = (int)response.StatusCode;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    thrown = e.GetType().Name;
                    body = string.Empty;
                    status = 0;
                }
            }

            stopwatch.Stop();
            sent++;
            statuses[status] = statuses.GetValueOrDefault(status) + 1;
            var ms = (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds);
            if (!warmUp) slowest[row.Route] = Math.Max(slowest.GetValueOrDefault(row.Route), ms);
            var problemType = Oracle.ProblemType(row.Pattern, body);
            if (part.Kind is PartKind.Json or PartKind.Jws)
            {
                jsonOrJws++;
                var past = thrown is null && Oracle.PastFirstParser((HttpStatusCode)status, problemType);
                if (past) pastParser++;
                if (row.Route == "POST /v1/posts" && part.Kind == PartKind.Json && part.Address.StartsWith("json:/envelope/", StringComparison.Ordinal))
                {
                    envelopeDraws++;
                    if (past) envelopePast++;
                }
            }

            var reason = thrown is null
                ? Oracle.Verdict(row.Pattern, (HttpStatusCode)status, body, stopwatch.Elapsed, warmUp)
                : thrown is nameof(TaskCanceledException) ? Oracle.Budget(stopwatch.Elapsed) + " (the client's 120 s timeout)" : "the send threw " + thrown;
            if (reason is null) continue;

            var label = ExpectedFaults.RandomPrefix + draw.ToString(CultureInfo.InvariantCulture);
            var ledgeredRow = ExpectedFaults.Rows.FirstOrDefault(r =>
                r.Route == row.Route && r.Variant == row.Variant && r.Part == part.Address && r.Variation == label && r.Copy == Copies.Wire(copy));
            var ledgered = ledgeredRow is not null && !reason.StartsWith(Oracle.OverBudget, StringComparison.Ordinal);
            await FuzzRun.AppendFailureAsync(row.Route, row.Variant, part.Address, label, Copies.Wire(copy), status, problemType, ms, ledgered, ct);
            if (ledgered)
            {
                matchedRandom.Add(ledgeredRow!);
                continue;
            }

            stoppedAt = draw;
            failure =
                $"seed {Seed}, draw {label} of {Draws.ToString(CultureInfo.InvariantCulture)}: " +
                $"{status.ToString(CultureInfo.InvariantCulture)} {row.Route} [{row.Variant}] {part.Address} {Copies.Wire(copy)}: {reason} " +
                $"({ms.ToString(CultureInfo.InvariantCulture)} ms); the value: {text.Describe()}";
        }

        // A random-N ledger row is this pass's to judge stale; the closed pass judges every other row (review of 9411deb).
        var stale = new List<string>();
        if (stoppedAt is { } stop)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"stale check skipped: the pass stopped at draw {stop.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            foreach (var row in ExpectedFaults.Rows.Where(r => ExpectedFaults.IsRandom(r) && !matchedRandom.Contains(r)))
                stale.Add($"ledger: {row.Register} ({row.Route} [{row.Variant}] {row.Part} {row.Variation} {row.Copy}) was not observed failing in the random pass (stale)");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"seed {Seed}: {sent.ToString(CultureInfo.InvariantCulture)} sent, {unsent.ToString(CultureInfo.InvariantCulture)} unsent; " +
            $"{pastParser.ToString(CultureInfo.InvariantCulture)} of {jsonOrJws.ToString(CultureInfo.InvariantCulture)} JSON and JWS draws past the first parser; " +
            $"{envelopePast.ToString(CultureInfo.InvariantCulture)} of {envelopeDraws.ToString(CultureInfo.InvariantCulture)} POST /v1/posts envelope draws past ADMIT; answers: " +
            string.Join(", ", statuses.Select(s => $"{s.Key.ToString(CultureInfo.InvariantCulture)} x{s.Value.ToString(CultureInfo.InvariantCulture)}")));
        if (Environment.GetEnvironmentVariable("CURIA_FUZZ_TIMINGS") is { } timings)
        {
            var lines = new StringBuilder();
            foreach (var (route, ms) in slowest)
                lines.Append(CultureInfo.InvariantCulture, $"slowest-random\t{route}\t-\t{ms}\n");
            await File.AppendAllTextAsync(timings, lines.ToString(), ct);
        }

        Assert.True(failure is null, failure);
        Assert.True(stale.Count == 0, string.Join('\n', stale));
        Assert.True(
            pastParser * Divisor >= jsonOrJws,
            $"seed {Seed}: {pastParser.ToString(CultureInfo.InvariantCulture)} of {jsonOrJws.ToString(CultureInfo.InvariantCulture)} JSON and JWS draws got past the first parser; the pass combines nothing past it");
        Assert.True(
            envelopeDraws > 0 && envelopePast * EnvelopeDivisor >= envelopeDraws,
            $"seed {Seed}: {envelopePast.ToString(CultureInfo.InvariantCulture)} of {envelopeDraws.ToString(CultureInfo.InvariantCulture)} POST /v1/posts envelope draws got past ADMIT; the pass combines nothing past it on the envelope");
        Assert.True(
            unsent * 10 <= Draws,
            $"seed {Seed}: {unsent.ToString(CultureInfo.InvariantCulture)} of {Draws.ToString(CultureInfo.InvariantCulture)} draws could not be built, so the pass sent too little to mean anything");
    }
}

/// <summary>
/// One hostile string (spec §4.10's CsCheck pass): a sequence of code points, lone surrogates among
/// them, and raw byte runs that are not UTF-8, written in each position's own rendering.
/// </summary>
internal sealed class HostileText
{
    private readonly IReadOnlyList<Unit> _units;

    private HostileText(IReadOnlyList<Unit> units) => _units = units;

    /// <summary>A code point (a lone surrogate included), or raw bytes that are not UTF-8.</summary>
    private sealed record Unit(int CodePoint, byte[]? Raw)
    {
        /// <summary>The unit's bytes: generalized UTF-8 for a code point, so a lone surrogate is ED A0 80, and raw bytes as they are.</summary>
        internal byte[] Bytes => Raw ?? Wtf8(CodePoint);
    }

    /// <summary>The raw byte runs: a byte never in UTF-8, an overlong NUL, an encoded surrogate, a stray continuation, beyond U+10FFFF, a truncated sequence.</summary>
    private static readonly byte[][] RawRuns =
    [
        [0xFF], [0xC0, 0x80], [0xED, 0xA0, 0x80], [0xED, 0xB0, 0x80], [0x80], [0xF5, 0x80, 0x80, 0x80], [0xE0, 0x80],
    ];

    /// <summary>Format characters (Cf).</summary>
    private static readonly int[] Format =
    [
        0x00AD, 0x0600, 0x061C, 0x06DD, 0x180E, 0x200B, 0x200C, 0x200D, 0x200E, 0x200F, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E,
        0x2060, 0x2061, 0x2062, 0x2063, 0x2064, 0x2066, 0x2067, 0x2068, 0x2069, 0xFEFF, 0xFFF9, 0xFFFA, 0xFFFB, 0x110BD, 0xE0001, 0xE0020, 0xE007F,
    ];

    private static Gen<Unit> CodePoints(Gen<int> gen) => gen.Select(c => new Unit(c, null));

    /// <summary>
    /// Every class in spec §4.10's list except the four that ADMIT refuses wherever they appear (raw
    /// invalid bytes, lone surrogates and both noncharacter classes, curia/admit/noncharacter; review of
    /// 6cbfa9f), weighted toward each hostile class, with printable ASCII the largest single class. C0,
    /// U+0000 included, stays: an escaped NUL passes the JSON parser and reaches the reader after it.
    /// </summary>
    private static readonly Gen<Unit> CleanUnitGen = Gen.Frequency(
        (2, CodePoints(Gen.Int[0x00, 0x1F])),                                             // C0
        (1, CodePoints(Gen.Int[0x80, 0x9F])),                                             // C1
        (2, CodePoints(Gen.OneOfConst(Format))),                                          // Cf
        (1, CodePoints(Gen.OneOfConst(0x2028, 0x2029))),                                  // Zl, Zp
        (2, CodePoints(Gen.Int[0x0300, 0x036F])),                                         // combining marks
        (2, CodePoints(Gen.Int[0x10000, 0x10FFFF])),                                      // astral
        (6, CodePoints(Gen.Int[0x20, 0x7E])));                                            // printable ASCII

    /// <summary>
    /// The full mixture: every class in spec §4.10's list, raw invalid bytes, lone surrogates and
    /// noncharacters among them. Each refuses the whole value at ADMIT, the first parser (escaped or
    /// raw), so they are drawn in one string in five (<see cref="Generator"/>), and the other four
    /// strings in five can pass ADMIT's parser (reviews of 9411deb and 6cbfa9f: drawn per unit, or
    /// noncharacters in every string, they refused nearly every value).
    /// </summary>
    private static readonly Gen<Unit> DecodingHostileUnitGen = Gen.Frequency(
        (2, CodePoints(Gen.Int[0x00, 0x1F])),                                             // C0
        (1, CodePoints(Gen.Int[0x80, 0x9F])),                                             // C1
        (2, CodePoints(Gen.Int[0xD800, 0xDFFF])),                                         // lone surrogates
        (1, CodePoints(Gen.Int[0xFDD0, 0xFDEF])),                                         // noncharacters
        (1, CodePoints(Gen.Int[0, 16].Select(Gen.Int[0, 1], (plane, low) => (plane << 16) | 0xFFFE | low))), // noncharacters, plane ends
        (2, CodePoints(Gen.OneOfConst(Format))),                                          // Cf
        (1, CodePoints(Gen.OneOfConst(0x2028, 0x2029))),                                  // Zl, Zp
        (2, CodePoints(Gen.Int[0x0300, 0x036F])),                                         // combining marks
        (2, CodePoints(Gen.Int[0x10000, 0x10FFFF])),                                      // astral
        (2, Gen.OneOfConst(RawRuns).Select(b => new Unit(0, b))),                         // raw invalid bytes
        (6, CodePoints(Gen.Int[0x20, 0x7E])));                                            // printable ASCII

    /// <summary>A length in bytes: 0 to 300, and one draw in twenty between 4 KiB and 64 KiB.</summary>
    private static readonly Gen<int> LengthGen = Gen.Frequency((19, Gen.Int[0, 300]), (1, Gen.Int[4_096, 65_536]));

    // Properties, not fields, so falsification F27 (plan Task A6), which leaves CleanStrings unread, still builds (a field unread is CA1823, an error here).
    private static Gen<HostileText> CleanStrings => LengthGen.SelectMany(n => CleanUnitGen.Array[n]).Select(WithinLength);

    private static Gen<HostileText> DecodingHostileStrings => LengthGen.SelectMany(n => DecodingHostileUnitGen.Array[n]).Select(WithinLength);

    /// <summary>
    /// A string of at most the drawn length in bytes. Every class in spec §4.10's list is still drawn;
    /// raw invalid bytes, lone surrogates and noncharacters are drawn in one string in five, so the
    /// other four strings in five can pass ADMIT's parser (review of 6cbfa9f).
    /// </summary>
    internal static readonly Gen<HostileText> Generator = Gen.Frequency((4, CleanStrings), (1, DecodingHostileStrings));

    /// <summary>Units are drawn, as many as the length, then the longest prefix within the length in bytes is kept.</summary>
    private static HostileText WithinLength(Unit[] units)
    {
        var limit = units.Length;
        var kept = new List<Unit>(units.Length);
        var bytes = 0;
        foreach (var unit in units)
        {
            if (bytes + unit.Bytes.Length > limit) break;
            bytes += unit.Bytes.Length;
            kept.Add(unit);
        }

        return new HostileText(kept);
    }

    /// <summary>The value at <paramref name="part"/>, in that position's rendering (spec §4.10).</summary>
    internal VariedValue At(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.IsBodyRoot) return new VariedValue.Body(Wire(), null);
        return part.Kind switch
        {
            PartKind.Json or PartKind.Jws => new VariedValue.RawJson(Json()),
            PartKind.Path or PartKind.Query or PartKind.Form => new VariedValue.Encoded(Percent()),
            PartKind.Header or PartKind.HeaderParameter => new VariedValue.HeaderText(Header()),
            _ => throw new ArgumentOutOfRangeException(nameof(part), part.Kind, "not a position"),
        };
    }

    /// <summary>For a failure line: the length, and the first bytes in hex, never the value as text.</summary>
    internal string Describe()
    {
        var wire = Wire();
        return $"{wire.Length.ToString(CultureInfo.InvariantCulture)} bytes, starting {Convert.ToHexStringLower(wire.AsSpan(0, Math.Min(64, wire.Length)))}";
    }

    private byte[] Wire() => [.. _units.SelectMany(u => u.Bytes)];

    /// <summary>A JSON string written by hand: C0, the quote, the backslash and a lone surrogate as escapes; raw runs as they are.</summary>
    private byte[] Json()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x22);
        foreach (var unit in _units)
        {
            if (unit.Raw is not null) stream.Write(unit.Raw);
            else if (unit.CodePoint == 0x22) stream.Write("\\\""u8);
            else if (unit.CodePoint == 0x5C) stream.Write("\\\\"u8);
            else if (unit.CodePoint < 0x20 || unit.CodePoint is >= 0xD800 and <= 0xDFFF)
                stream.Write(Encoding.ASCII.GetBytes("\\" + "u" + unit.CodePoint.ToString("x4", CultureInfo.InvariantCulture)));
            else stream.Write(unit.Bytes);
        }

        stream.WriteByte(0x22);
        return stream.ToArray();
    }

    /// <summary>Every byte outside RFC 3986's unreserved set percent-encoded.</summary>
    private string Percent()
    {
        var builder = new StringBuilder();
        foreach (var b in Wire())
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>A header's text: code points as UTF-16, a lone surrogate as itself, raw runs as Latin-1 characters.</summary>
    private string Header()
    {
        var builder = new StringBuilder();
        foreach (var unit in _units)
        {
            if (unit.Raw is not null) builder.Append(Encoding.Latin1.GetString(unit.Raw));
            else if (unit.CodePoint is >= 0xD800 and <= 0xDFFF) builder.Append((char)unit.CodePoint);
            else builder.Append(char.ConvertFromUtf32(unit.CodePoint));
        }

        return builder.ToString();
    }

    /// <summary>UTF-8 generalized to the surrogate range.</summary>
    private static byte[] Wtf8(int c) => c switch
    {
        < 0x80 => [(byte)c],
        < 0x800 => [(byte)(0xC0 | (c >> 6)), (byte)(0x80 | (c & 0x3F))],
        < 0x10000 => [(byte)(0xE0 | (c >> 12)), (byte)(0x80 | ((c >> 6) & 0x3F)), (byte)(0x80 | (c & 0x3F))],
        _ => [(byte)(0xF0 | (c >> 18)), (byte)(0x80 | ((c >> 12) & 0x3F)), (byte)(0x80 | ((c >> 6) & 0x3F)), (byte)(0x80 | (c & 0x3F))],
    };
}
