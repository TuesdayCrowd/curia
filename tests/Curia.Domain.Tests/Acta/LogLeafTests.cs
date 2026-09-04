using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Canon.Json;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Tests.Acta;

/// <summary>
/// R6.46 as the domain computes it, held to <c>conformance/acta/</c>: the corpus pins the bytes
/// of a leaf input, and this is the code that renders an <see cref="AppendedEvent"/> into them.
/// <c>Curia.Canon.Tests</c> checks the same vectors from the canonicalizer's side; this checks
/// the rendering -- <c>actor_id</c> as null, <c>server_ts</c> at six digits -- which the
/// canonicalizer never sees.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LogLeafTests
{
    private static readonly string ConformanceRoot = FindConformance();

    private static string FindConformance()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        return dir is null
            ? throw new InvalidOperationException("conformance/ not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "conformance");
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static JsonValue? Member(JsonValue.Object o, string name) =>
        o.Members.FirstOrDefault(m => m.Key == name).Value;

    /// <summary>An <see cref="AppendedEvent"/> reconstructed from a vector's entry document.</summary>
    private static AppendedEvent EventFrom(byte[] input)
    {
        var doc = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(input));
        string Str(string name) => ((JsonValue.String)Member(doc, name)!).Value;

        var actor = Member(doc, LogLeaf.ActorIdMember) is JsonValue.String a ? Require(ActorId.Create(a.Value)) : (ActorId?)null;
        var timestamp = DateTimeOffset.Parse(Str(LogLeaf.ServerTimestampMember), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        return new AppendedEvent(
            new EventSequence(1),
            Require(AggregateId.Create(Str(LogLeaf.AggregateIdMember))),
            ServerTimestamp.At(timestamp),
            new DomainEvent(
                Require(EventId.Create(Str(LogLeaf.EventIdMember))),
                Require(EventType.Create(Str(LogLeaf.EventTypeMember))),
                actor,
                Member(doc, LogLeaf.PayloadMember)!));
    }

    [Theory]
    [InlineData("content-entry")]
    [InlineData("content-entry-unordered")]
    [InlineData("null-actor-entry")]
    [InlineData("head-entry")]
    [InlineData("nfd-payload-stays-nfd")]
    public void R6_46_AnEventRendersToTheConformanceVectorsLeafInputAndLeaf(string vector)
    {
        var dir = Path.Combine(ConformanceRoot, "acta", vector);
        var appended = EventFrom(File.ReadAllBytes(Path.Combine(dir, "input.json")));

        var input = Require(LogLeaf.Input(appended)).ToArray();
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "expected.canonical")), input);
        Assert.Equal(
            File.ReadAllText(Path.Combine(dir, "expected.leaf")).Trim(),
            Convert.ToHexStringLower([.. Require(LogLeaf.Hash(appended))]));
    }

    [Fact]
    public void R6_46_ServerTsRendersInUtcWithExactlySixFractionalDigits()
    {
        // 18:00 at +02:00 is 16:00Z; 1234567 ticks is 123.4567 ms, and the seventh digit is dropped.
        var instant = new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        Assert.Equal("2026-09-04T16:00:00.123456Z", LogLeaf.RenderServerTimestamp(ServerTimestamp.At(instant)));
    }

    [Fact]
    public void R6_46_AnAbsentActorIsJsonNullNotAnAbsentMember()
    {
        var appended = EventFrom(File.ReadAllBytes(Path.Combine(ConformanceRoot, "acta", "null-actor-entry", "input.json")));
        Assert.Null(appended.Event.Actor);

        var entry = LogLeaf.Entry(appended);
        Assert.Equal(6, entry.Members.Length);
        Assert.IsType<JsonValue.Null>(Member(entry, LogLeaf.ActorIdMember));
    }

    [Fact]
    public void R6_49_TheHeadDocumentIsExactlyThreeMembersInCanonicalOrder()
    {
        var root = ImmutableArray.CreateRange(Enumerable.Repeat((byte)0x11, 32));
        var head = LogEntries.HeadDocument(root, "2026-09-04T17:00:00.000000Z", 8);

        Assert.Equal(
            "{\"root_hash\":\"sha256:" + new string('1', 64) + "\",\"timestamp\":\"2026-09-04T17:00:00.000000Z\",\"tree_size\":8}",
            Encoding.UTF8.GetString(Require(LogEntries.HeadCanonical(head)).ToArray()));
    }

    [Fact]
    public void TheWireDigestFormIsStrictAndRoundTrips()
    {
        var hash = ImmutableArray.CreateRange(Enumerable.Range(0, 32).Select(i => (byte)(i * 7)));
        var text = LogEntries.Prefixed(hash);

        Assert.StartsWith("sha256:", text, StringComparison.Ordinal);
        Assert.Equal(hash, LogEntries.Unprefixed(text)!.Value);
        Assert.Null(LogEntries.Unprefixed(text.ToUpperInvariant()));
        Assert.Null(LogEntries.Unprefixed(text[7..]));
        Assert.Null(LogEntries.Unprefixed(text[..^1]));
        Assert.Null(LogEntries.Unprefixed(null));
    }
}
