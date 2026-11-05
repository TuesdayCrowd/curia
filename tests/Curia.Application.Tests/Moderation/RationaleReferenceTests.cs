using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Domain.Screening;
using Xunit;
using Curia.Tests.Shared;

namespace Curia.Application.Tests.Moderation;

/// <summary>
/// R10.96 (errata G60), RNC15, at the two writers that screen a rationale as bare text: a flag's and a moderation record's. A rationale that quotes a reference
/// (<c>the log shows password=$DB_PASS</c>) is accepted and a rationale that carries a value is refused as before, naming the screening failure and holding no
/// part of the value. Written red: the reference leg fails by assertion on the seam, which reports it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RationaleReferenceTests
{
    private const string Post = "01JP0ST0000000000000000001";
    private const string Reporter = "https://agents.example/reporter";
    private const string Reference = "the log shows password=$DB_PASS";
    private const string Value = "the log shows password=EXAMPLE-NOT-A-REAL-SECRET";

    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Digest = "sha256:" + new string('a', 64);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static async Task<(InMemoryEventStore Store, InMemoryFlagDetailStore Details, ManualTimeProvider Clock)> WorldAsync(CancellationToken ct)
    {
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        Require(await store.AppendAsync(
            Require(AggregateId.Create(Post)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(Post)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create("https://agents.example/author")),
                new JsonValue.Object(
                [
                    new("post_id", new JsonValue.String(Post)),
                    new("canonical", new JsonValue.String("{\"body\":\"a question\"}")),
                    new("signature", new JsonValue.String("sig")),
                    new("digest", new JsonValue.String(Digest)),
                    new("author", new JsonValue.String("https://agents.example/author")),
                    new("board", new JsonValue.String("board-1")),
                    new("kind", new JsonValue.String("question")),
                ]))],
            ct).ConfigureAwait(false));
        return (store, new InMemoryFlagDetailStore(), clock);
    }

    private static async Task<int> LogLengthAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false)).Count;

    /// <summary>RNC15, the flag: a rationale quoting a reference is recorded; one carrying a value is refused <c>rationale-rejected</c>, writes nothing and echoes none of the value.</summary>
    [Fact]
    public async Task R10_96_RNC15_AFlagRationaleThatQuotesAReferenceIsRecordedAndOneThatCarriesAValueIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, details, clock) = await WorldAsync(ct);
        var before = await LogLengthAsync(store, ct);

        var value = await new RaiseFlag(store, details, clock).RecordAsync(PostId.Parse(Post), Typed.Agent(Reporter), FlagKind.CredentialLeak, Rationale.Parse(Value), ct);
        Assert.False(value.TryGetValue(out _, out var refusal), "the value leg: expected a refusal");
        Assert.Equal("curia/flag/rationale-rejected", refusal!.Type);
        Assert.DoesNotContain("EXAMPLE-NOT-A-REAL-SECRET", $"{refusal.Title}{refusal.Detail}", StringComparison.Ordinal);
        Assert.Equal(before, await LogLengthAsync(store, ct));

        var reference = await new RaiseFlag(store, details, clock).RecordAsync(PostId.Parse(Post), Typed.Agent(Reporter), FlagKind.CredentialLeak, Rationale.Parse(Reference), ct);
        Assert.True(reference.TryGetValue(out _, out var error), $"expected accepted, found screening refusal {error?.Type}");
    }

    /// <summary>RNC15, the moderation record: the same two legs through <c>ApplyModeration</c>.</summary>
    [Fact]
    public async Task R10_96_RNC15_AModerationReasonThatQuotesAReferenceIsRecordedAndOneThatCarriesAValueIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, details, clock) = await WorldAsync(ct);
        var operatorId = Require(ActorId.Create("operator:reviewer"));
        var before = await LogLengthAsync(store, ct);

        var value = await new ApplyModeration(store, details, clock).RecordAsync(PostId.Parse(Post), ModerationEffect.Withhold, FlagKind.CredentialLeak, Rationale.Parse(Value), operatorId, ct);
        Assert.False(value.TryGetValue(out _, out var refusal), "the value leg: expected a refusal");
        Assert.Equal("curia/moderation/rationale-rejected", refusal!.Type);
        Assert.DoesNotContain("EXAMPLE-NOT-A-REAL-SECRET", $"{refusal.Title}{refusal.Detail}", StringComparison.Ordinal);
        Assert.Equal(before, await LogLengthAsync(store, ct));

        var reference = await new ApplyModeration(store, details, clock).RecordAsync(PostId.Parse(Post), ModerationEffect.Withhold, FlagKind.CredentialLeak, Rationale.Parse(Reference), operatorId, ct);
        Assert.True(reference.TryGetValue(out _, out var error), $"expected accepted, found screening refusal {error?.Type}");
    }

    /// <summary>
    /// RNC23: the Forum's own words are unchanged: the title the connection-string refusal carries equals the literal recorded from the unmodified tree (Task 0, M3), with
    /// its type and its detail naming the category and an offset. CONTROL: green on the seam, and red if the Forum's wording moves.
    /// </summary>
    [Fact]
    public void R11_84_RNC23_TheForumsOwnRefusalIsWordedAsItWas()
    {
        var annotations = RiskAnnotations.Create(
            [new RiskFlag(RiskCategory.ConnectionStringPassword, 81, 10, SecretScanner.Version)],
            [SecretScanner.Version]);

        var error = IngestErrors.ScreeningRejected(annotations);

        Assert.Equal("curia/ingest/screening-rejected", error.Type);
        Assert.Equal(
            "The submission contains credential material and was rejected. Rotate the credential; it cannot be redacted after signing, so nothing was stored.",
            error.Title);
        Assert.Equal("ConnectionStringPassword@81", error.Detail);
    }
}
