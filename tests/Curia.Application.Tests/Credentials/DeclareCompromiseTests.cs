using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R6.26 (rev.) and R6.57 at the declaration use case (errata G21, spec sections 4.5, 4.8): a declaration
/// is signed by a published log key under its own <c>typ</c>, its <c>t_c</c> is floored at the binding, and
/// its cascade is transitive over rotations at or after <c>from_index</c> and no earlier.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DeclareCompromiseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ActorId Operator = Require(ActorId.Create("operator:ops"));

    private static string Describe<T>(Result<T> result) => result.Match(_ => "ok", e => $"{e.Type} {e.Detail}");

    /// <summary>Publishes <paramref name="signer"/>'s key as the log does (<c>log.key</c>, R6.50), as the operator's sign-head does.</summary>
    private static async Task PublishAsync(KeyActHarness harness, InMemoryLifecycleStatementSigner signer)
    {
        var acta = Require(ActaLog.Fold(Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false))));
        var payload = new JsonValue.Object(
        [
            new(LogEntries.KidMember, new JsonValue.String(signer.Kid)),
            new(LogEntries.JwkMember, signer.PublicJwk()),
            new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
        ]);
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(LogEntries.KeysAggregate)),
            Require(AggregateVersion.From(acta.Keys.Length)),
            [new DomainEvent(Require(EventId.Create("01K4CQ1V9P3R5T7W9Y1A3C5EKY")), Require(EventType.Create(LogEntries.KeyType)), Operator, payload)],
            Ct).ConfigureAwait(false));
    }

    private static Task<Result<CompromiseRecorded>> DeclareAsync(
        KeyActHarness harness, InMemoryLifecycleStatementSigner signer, string kid, long fromIndex, string agent = Alice) =>
        new DeclareCompromise(harness.Events, harness.Clock)
            .DeclareAsync(Typed.Agent(agent), Typed.Kid(kid), fromIndex, CompromiseReason.OwnerReported, signer, Operator, Ct);

    private static async Task<Dictionary<string, KeyStatus>> StatusesAsync(KeyActHarness harness, params string[] kids)
    {
        var lifecycle = Require(KeyLifecycle.Of(Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false)), Typed.Agent(Alice)));
        return kids.ToDictionary(k => k, lifecycle.StatusNow, StringComparer.Ordinal);
    }

    private static string Render(Dictionary<string, KeyStatus> statuses) =>
        string.Join(", ", statuses.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} {s.Value}"));

    /// <summary>Alice enrolled with alice-1 (bound at index 1); each next key is rotated in by the one before.</summary>
    private static async Task<(KeyActHarness Harness, TestIdentity[] Keys, InMemoryLifecycleStatementSigner Signer)> ChainAsync(int keys)
    {
        var harness = new KeyActHarness();
        var identities = Enumerable.Range(1, keys).Select(i => TestIdentity.New($"alice-{i}")).ToArray();
        await harness.EnrollAsync(Alice, identities[0]).ConfigureAwait(false);
        for (var i = 1; i < keys; i++)
            Require(await harness.RotateAsync(Alice, identities[i - 1], identities[i]).ConfigureAwait(false));

        var signer = new InMemoryLifecycleStatementSigner();
        await PublishAsync(harness, signer).ConfigureAwait(false);
        return (harness, identities, signer);
    }

    [Fact]
    public async Task R6_26_TcBelowTheBindingIsRefused()
    {
        // 0 enrolled, 1 binds alice-1, 2 the rotation binding alice-2, 3 the log key.
        var (harness, _, signer) = await ChainAsync(2);
        using var _ = signer;
        var before = (await harness.StreamAsync(Alice)).Count;

        var below = await DeclareAsync(harness, signer, "alice-2", fromIndex: 1);
        var atBinding = await DeclareAsync(harness, signer, "alice-2", fromIndex: 2);

        Assert.Equal(
            $"curia/keys/before-binding field=from_index; ok; {before + 1}",
            $"{Describe(below)}; {Describe(atBinding)}; {(await harness.StreamAsync(Alice)).Count}");
    }

    [Fact]
    public async Task R6_57_AFromIndexNotBelowTheDeclarationIsRefused()
    {
        var (harness, _, signer) = await ChainAsync(2);
        using var _ = signer;

        // The declaration would take index 4, so 4 is not below it.
        var atDeclaration = await DeclareAsync(harness, signer, "alice-1", fromIndex: 4);
        var beyond = await DeclareAsync(harness, signer, "alice-1", fromIndex: 400);
        var last = await DeclareAsync(harness, signer, "alice-1", fromIndex: 3);

        Assert.Equal(
            "curia/keys/from-index-beyond-log field=from_index; curia/keys/from-index-beyond-log field=from_index; ok",
            $"{Describe(atDeclaration)}; {Describe(beyond)}; {Describe(last)}");
    }

    [Fact]
    public async Task R6_26_ALogKeyTheLogHasNotPublishedIsRefusedAndNothingIsWritten()
    {
        var (harness, _, signer) = await ChainAsync(2);
        using var published = signer;
        using var stranger = new InMemoryLifecycleStatementSigner("another-log-key");
        var before = Require(await harness.Events.ReadAllAsync(Ct)).Count;

        var answer = await DeclareAsync(harness, stranger, "alice-1", fromIndex: 1);
        var control = await DeclareAsync(harness, published, "alice-1", fromIndex: 1);

        Assert.Equal(
            $"curia/keys/log-key-not-published run sign-head with this key first; ok; {before + 1}",
            $"{Describe(answer)}; {Describe(control)}; {Require(await harness.Events.ReadAllAsync(Ct)).Count}");
    }

    [Fact]
    public async Task R6_57_AKeyTheCompromisedKeyRotatedInAfterTcIsCompromised()
    {
        var (harness, _, signer) = await ChainAsync(2);
        using var _ = signer;

        var declared = Require(await DeclareAsync(harness, signer, "alice-1", fromIndex: 1));

        Assert.Equal(
            "alice-1, alice-2; alice-1 Compromised, alice-2 Compromised",
            $"{string.Join(", ", declared.Kids)}; {Render(await StatusesAsync(harness, "alice-1", "alice-2"))}");
    }

    [Fact]
    public async Task R6_57_AKeyItRotatedInBeforeTcStands()
    {
        // 0 enrolled, 1 binds alice-1, 2 binds alice-2 (authorized by alice-1), 3 the log key. from_index 3 is after the rotation.
        var (harness, _, signer) = await ChainAsync(2);
        using var _ = signer;

        var declared = Require(await DeclareAsync(harness, signer, "alice-1", fromIndex: 3));

        Assert.Equal(
            "alice-1; alice-1 Compromised, alice-2 Active",
            $"{string.Join(", ", declared.Kids)}; {Render(await StatusesAsync(harness, "alice-1", "alice-2"))}");
    }

    [Fact]
    public async Task R6_57_TheCascadeIsTransitive()
    {
        var (harness, _, signer) = await ChainAsync(3);
        using var _ = signer;

        var declared = Require(await DeclareAsync(harness, signer, "alice-1", fromIndex: 1));

        Assert.Equal(
            "alice-1, alice-2, alice-3; alice-1 Compromised, alice-2 Compromised, alice-3 Compromised",
            $"{string.Join(", ", declared.Kids)}; {Render(await StatusesAsync(harness, "alice-1", "alice-2", "alice-3"))}");
    }

    [Fact]
    public async Task R6_57_ASiblingTheCompromisedKeyDidNotBindIsNotInTheCascade()
    {
        // alice-1 rotates in alice-2; alice-2 rotates in alice-3; declaring alice-2 compromised from its binding leaves alice-1 and ends alice-3.
        var (harness, _, signer) = await ChainAsync(3);
        using var _ = signer;

        var declared = Require(await DeclareAsync(harness, signer, "alice-2", fromIndex: 2));

        Assert.Equal(
            "alice-2, alice-3; alice-1 Active, alice-2 Compromised, alice-3 Compromised",
            $"{string.Join(", ", declared.Kids)}; {Render(await StatusesAsync(harness, "alice-1", "alice-2", "alice-3"))}");
    }

    [Fact]
    public async Task R6_26_TheDeclarationIsSignedUnderItsOwnTypWithTheLogKeysKid()
    {
        var (harness, _, signer) = await ChainAsync(2);
        using var _ = signer;
        Require(await DeclareAsync(harness, signer, "alice-1", fromIndex: 1));

        var stream = await harness.StreamAsync(Alice);
        var entry = stream[^1];
        var payload = (JsonValue.Object)entry.Event.Payload;
        var statement = ((JsonValue.String)payload.Members.Single(m => m.Key == "statement").Value).Value;
        var signature = new JwsSignature(((JsonValue.String)payload.Members.Single(m => m.Key == "declaration").Value).Value);
        var canonical = Require(CanonicalJsonOf(statement));

        var header = KeyStatements.CheckHeader(signature, "declaration", KeyStatements.CompromiseHeaderTyp, signer.Kid);
        var verifies = new DetachedJws(new Dictionary<string, IContentSigner>(), harness.Verifiers, KeyStatements.CompromiseHeaderTyp)
            .Verify(canonical, signature, signer.Material);
        var asHead = new DetachedJws(new Dictionary<string, IContentSigner>(), harness.Verifiers, DetachedJws.HeadTyp)
            .Verify(canonical, signature, signer.Material);

        Assert.Equal(
            $"True; True; False; {LifecycleAppendGuard.KeyCompromisedType}; {Operator.Value}",
            $"{header.IsOk}; {verifies.IsOk}; {asHead.IsOk}; {entry.Event.Type.Value}; {entry.Event.Actor?.Value}");
    }

    private static Result<CanonicalBytes> CanonicalJsonOf(string statement) =>
        JsonReader.Parse(System.Text.Encoding.UTF8.GetBytes(statement), AdmitLimits.Default).Bind(CanonicalJson.CanonicalizeWithNfc);
}
