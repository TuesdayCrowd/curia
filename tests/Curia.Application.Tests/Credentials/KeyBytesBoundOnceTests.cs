using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Credentials;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;
using static Curia.Application.Tests.Credentials.LifecycleProbeSupport;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// Errata G78 (R4.45, R6.64 (add.)), the rotation use case's half: a rotation whose statement names the public key of any key the honoured prefix has bound is refused <c>curia/keys/key-already-bound</c>, after the refusal
/// for a <c>kid</c> already named, before R7.23's caps and before the key is registered (KBO11 to KBO13). Each scene is built the way <c>KeyActHarness</c> builds one, with real ES256 keys; the five shapes are those of the
/// honoured vectors KBO1 to KBO5 (the vectors are Ed25519 and this harness verifies ES256, so a scene reproduces the shape and not the file: a deviation recorded in the task's report). Written against the tree before the rule
/// exists: the refusals are red by their own assertion (the rotation is honoured), the controls are green.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyBytesBoundOnceTests
{
    private const string AlreadyBound = "curia/keys/key-already-bound";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ActorId Operator = Require(ActorId.Create("operator:ops"));

    private static string TypeOf<T>(Result<T> result) => result.Match(_ => "ok", e => e.Type);

    private static async Task<string> WrittenAsync(KeyActHarness harness) =>
        $"log={Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false)).Count} store={(await harness.Registry.KeysForAsync(Typed.Agent(Alice), Ct).ConfigureAwait(false)).Count}";

    /// <summary>The same key, as the Forum stores it, under another <c>kid</c>.</summary>
    private static TestIdentity Relabel(TestIdentity key, string kid)
    {
        var again = new TestIdentity(new PublicKeyMaterial(key.Material.Alg, kid, key.Material.Public.ToArray()), new SigningKey(key.Signing.Alg, kid, key.Signing.Private.ToArray()));
        KnownKeys.Remember(again.Material, again.Signing.Private.ToArray());
        return again;
    }

    private static async Task PublishDeclarationKeyAsync(KeyActHarness harness, InMemoryLifecycleStatementSigner signer)
    {
        var acta = Require(ActaLog.Fold(Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false))));
        var payload = new JsonValue.Object(
        [
            new(LogEntries.KidMember, new JsonValue.String(signer.Kid)),
            new(LogEntries.JwkMember, signer.PublicJwk()),
            new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
        ]);
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(LogEntries.DeclarationKeysAggregate)),
            Require(AggregateVersion.From(acta.DeclarationKeys.Length)),
            [new DomainEvent(Require(EventId.Create("01K4CQ1V9P3R5T7W9Y1A3C5EKY")), Require(EventType.Create(LogEntries.DeclarationKeyType)), Operator, payload)],
            Ct).ConfigureAwait(false));
    }

    private static async Task DeclareAsync(KeyActHarness harness, InMemoryLifecycleStatementSigner signer, string kid, long fromIndex) =>
        Require(await new DeclareCompromise(harness.Events, TestVerifier.Es256, harness.Clock)
            .DeclareAsync(Typed.Agent(Alice), Typed.Kid(kid), fromIndex, CompromiseReason.OwnerReported, signer, Operator, Ct).ConfigureAwait(false));

    /// <summary>The scene of each shape, and the rotation that names bound bytes under a new kid: authorizer, the key whose bytes it names, and the kid they come back under.</summary>
    private static async Task<(KeyActHarness Harness, TestIdentity Authorizer, TestIdentity Reborn)> SceneAsync(string shape)
    {
        var harness = new KeyActHarness();
        var k0 = TestIdentity.New("alice-1");
        await harness.EnrollAsync(Alice, k0).ConfigureAwait(false);
        using var signer = new InMemoryLifecycleStatementSigner();

        switch (shape)
        {
            case "retired":
            {
                var k1 = TestIdentity.New("alice-2");
                Require(await harness.RotateAsync(Alice, k0, k1).ConfigureAwait(false));
                Require(await harness.RetireAsync(Alice, k0).ConfigureAwait(false));
                return (harness, k1, Relabel(k0, "alice-3"));
            }

            case "compromised":
            {
                var k1 = TestIdentity.New("alice-2");
                Require(await harness.RotateAsync(Alice, k0, k1).ConfigureAwait(false));
                await PublishDeclarationKeyAsync(harness, signer).ConfigureAwait(false);
                await DeclareAsync(harness, signer, k0.Kid, 3).ConfigureAwait(false);
                return (harness, k1, Relabel(k0, "alice-3"));
            }

            case "cascaded":
            {
                var k9 = TestIdentity.New("alice-9");
                var k1 = TestIdentity.New("alice-2");
                Require(await harness.RotateAsync(Alice, k0, k9).ConfigureAwait(false));
                Require(await harness.RotateAsync(Alice, k0, k1).ConfigureAwait(false));
                await PublishDeclarationKeyAsync(harness, signer).ConfigureAwait(false);
                await DeclareAsync(harness, signer, k0.Kid, 3).ConfigureAwait(false);
                return (harness, k9, Relabel(k1, "alice-3"));
            }

            case "active":
            {
                var k1 = TestIdentity.New("alice-2");
                Require(await harness.RotateAsync(Alice, k0, k1).ConfigureAwait(false));
                return (harness, k0, Relabel(k1, "alice-3"));
            }

            case "self":
                return (harness, k0, Relabel(k0, "alice-3"));

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "not one of KBO11's shapes");
        }
    }

    public static TheoryData<string> Shapes => new() { "retired", "compromised", "cascaded", "active", "self" };

    /// <summary>
    /// KBO11: a rotation that names bound bytes is refused <c>key-already-bound</c> and leaves the log and the key store as they were: for a retired key (KBO1), a key declared compromised (KBO2), one compromised
    /// by the cascade alone (KBO3), a live key (KBO4) and the authorizing key's own bytes (the ES256 representation control, KBO5). Red on the tree: <c>Expected: curia/keys/key-already-bound; ...  Actual: ok; ...</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task KBO11_ARotationNamingBoundBytesIsRefusedAndWritesNothing(string shape)
    {
        var (harness, authorizer, reborn) = await SceneAsync(shape).ConfigureAwait(true);
        var before = await WrittenAsync(harness).ConfigureAwait(true);

        var answer = await harness.RotateAsync(Alice, authorizer, reborn).ConfigureAwait(true);

        Assert.Equal($"{AlreadyBound}; {before}", $"{TypeOf(answer)}; {await WrittenAsync(harness).ConfigureAwait(true)}");
    }

    /// <summary>KBO11, the refusal names no key: its detail holds no <c>kid</c> of the identity. Red on the tree: the rotation is honoured and there is no refusal to read.</summary>
    [Fact]
    public async Task KBO11_TheRefusalNamesNoKid()
    {
        var (harness, authorizer, reborn) = await SceneAsync("retired").ConfigureAwait(true);

        var answer = await harness.RotateAsync(Alice, authorizer, reborn).ConfigureAwait(true);

        var said = answer.Match(_ => "no refusal", e => $"{e.Type}: {e.Title} {e.Detail}");
        Assert.Equal("refused naming no kid: True", $"refused naming no kid: {said.StartsWith(AlreadyBound, StringComparison.Ordinal) && !said.Contains("alice-", StringComparison.Ordinal)}");
    }

    /// <summary>KBO11, control (order): the same <c>kid</c> and the same bytes are still <c>kid-taken</c>, which G74's settling reads. Green on the tree and after.</summary>
    [Fact]
    public async Task KBO11_TheSameKidAndTheSameBytesAreStillKidTaken()
    {
        var harness = new KeyActHarness();
        var k0 = TestIdentity.New("alice-1");
        var k1 = TestIdentity.New("alice-2");
        await harness.EnrollAsync(Alice, k0).ConfigureAwait(true);
        Require(await harness.RotateAsync(Alice, k0, k1).ConfigureAwait(true));
        var before = await WrittenAsync(harness).ConfigureAwait(true);

        var answer = await harness.RotateAsync(Alice, k0, k1).ConfigureAwait(true);

        Assert.Equal($"curia/keys/kid-taken; {before}", $"{TypeOf(answer)}; {await WrittenAsync(harness).ConfigureAwait(true)}");
    }

    /// <summary>
    /// KBO11, control (the over-broad reading, KBO7's shape): bytes only a row the procedure skipped stated are not bound, so a genuine rotation to them under another <c>kid</c> is honoured and appended. Green on the
    /// tree and after; red only if the set is built from every row parsed (FKBO5).
    /// </summary>
    [Fact]
    public async Task KBO11_BytesOnlyASkippedRowStatedAreNotBound()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var stranger = TestIdentity.New("attacker-1");
        var forged = await AppendRowAsync(harness, Alice, "agent.key-bound", Rotation(Alice, victim.Kid, stranger.Material, 1)).ConfigureAwait(true);
        Assert.Contains(VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)).For(Typed.Agent(Alice)).Skipped, r => r.LogIndex == forged);

        // prev is the last row the procedure honours (1), not the forged row at 2: a skipped row moves nothing (R6.64).
        var answer = await harness.RotateAsync(Alice, victim, Relabel(stranger, "alice-2"), prev: 1).ConfigureAwait(true);

        Assert.Equal("ok", TypeOf(answer));
    }

    /// <summary>KBO11, control (R4.32): an orphan row of the same <c>kid</c> and bytes in the store (an earlier attempt failed after registering) is reused, and the rotation is honoured. Green on the tree and after.</summary>
    [Fact]
    public async Task KBO11_AnOrphanStoreRowOfTheSameKidAndBytesIsStillReused()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var next = TestIdentity.New("alice-2");
        Require(await harness.Registry.AddAsync(Typed.Agent(Alice), Copy(next.Material), harness.Clock.GetUtcNow(), Ct).ConfigureAwait(true));

        var answer = await harness.RotateAsync(Alice, victim, next).ConfigureAwait(true);

        Assert.Equal("ok", TypeOf(answer));
    }

    /// <summary>
    /// KBO11, control (Amendment 1 ruling 23): the bytes refusal comes before the three caps. With R7.23's daily budget reached by three honoured rotations, a rotation naming a retired key's bytes is
    /// <c>key-already-bound</c>, not the budget refusal: no wait will make it honoured. Red on the tree: the budget refusal.
    /// </summary>
    [Fact]
    public async Task KBO11_TheBytesRefusalComesBeforeTheBudget()
    {
        var harness = new KeyActHarness();
        var (k0, k1, k2, k3) = (TestIdentity.New("alice-1"), TestIdentity.New("alice-2"), TestIdentity.New("alice-3"), TestIdentity.New("alice-4"));
        await harness.EnrollAsync(Alice, k0).ConfigureAwait(true);
        Require(await harness.RotateAsync(Alice, k0, k1).ConfigureAwait(true));
        Require(await harness.RetireAsync(Alice, k0).ConfigureAwait(true));
        Require(await harness.RotateAsync(Alice, k1, k2).ConfigureAwait(true));
        Require(await harness.RetireAsync(Alice, k1).ConfigureAwait(true));
        Require(await harness.RotateAsync(Alice, k2, k3).ConfigureAwait(true));
        var before = await WrittenAsync(harness).ConfigureAwait(true);

        var answer = await harness.RotateAsync(Alice, k3, Relabel(k0, "alice-9")).ConfigureAwait(true);

        Assert.Equal($"{AlreadyBound}; {before}", $"{TypeOf(answer)}; {await WrittenAsync(harness).ConfigureAwait(true)}");
    }

    private static TestIdentity Negated(TestIdentity key, string kid)
    {
        using var original = ECDsa.Create();
        original.ImportPkcs8PrivateKey(key.Signing.Private.Span, out _);
        var parameters = original.ExportParameters(includePrivateParameters: true);
        var prime = BigInteger.Parse("0FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var order = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        static byte[] Bytes32(BigInteger value)
        {
            var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            return raw.Length == 32 ? raw : [.. new byte[32 - raw.Length], .. raw];
        }

        var y = new BigInteger(parameters.Q.Y, isUnsigned: true, isBigEndian: true);
        var d = new BigInteger(parameters.D, isUnsigned: true, isBigEndian: true);
        using var negated = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Bytes32(order - d),
            Q = new ECPoint { X = parameters.Q.X, Y = Bytes32(prime - y) },
        });
        var identity = new TestIdentity(new PublicKeyMaterial("ES256", kid, negated.ExportSubjectPublicKeyInfo()), new SigningKey("ES256", kid, negated.ExportPkcs8PrivateKey()));
        KnownKeys.Remember(identity.Material, identity.Signing.Private.ToArray());
        return identity;
    }

    /// <summary>
    /// KBO12: an ES256 key and its negation (the same <c>x</c>, <c>y</c> replaced by <c>p - y</c>) are two keys: the comparison is over every coordinate, so a rotation to the negation is honoured. Green on the tree and
    /// after; red only if the comparison reads <c>x</c> alone (FKBO7).
    /// </summary>
    [Fact]
    public async Task KBO12_AKeyAndItsNegationAreTwoKeys()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var negated = Negated(victim, "alice-2");
        Assert.True(victim.Material.Public.ToArray().AsSpan(27, 32).SequenceEqual(negated.Material.Public.ToArray().AsSpan(27, 32)), "the scene's two keys share an x coordinate");
        Assert.False(victim.Material.Public.Span.SequenceEqual(negated.Material.Public.Span), "the scene's two keys are one key");

        var answer = await harness.RotateAsync(Alice, victim, negated).ConfigureAwait(true);

        Assert.Equal("ok", TypeOf(answer));
    }

    /// <summary>
    /// KBO13 (Amendment 1 ruling 13): <c>KeyActs.LifecycleRead</c> carries the honoured events' <c>Binding</c>, which lists every key ever bound in a log with a retirement, a declaration and a cascade, in the order bound,
    /// with the public key each was bound with: what <c>RotateKey</c> compares against. Red on the tree: <c>Binding</c> is not a member (the property is read by name, so the probe compiles before the member exists).
    /// </summary>
    [Fact]
    public async Task KBO13_TheLifecycleReadCarriesEveryKeyEverBoundInTheOrderBound()
    {
        var harness = new KeyActHarness();
        var (k0, k9, k1) = (TestIdentity.New("alice-1"), TestIdentity.New("alice-9"), TestIdentity.New("alice-2"));
        await harness.EnrollAsync(Alice, k0).ConfigureAwait(true);
        Require(await harness.RotateAsync(Alice, k0, k9).ConfigureAwait(true));
        Require(await harness.RotateAsync(Alice, k0, k1).ConfigureAwait(true));
        Require(await harness.RetireAsync(Alice, k9).ConfigureAwait(true));
        using var signer = new InMemoryLifecycleStatementSigner();
        await PublishDeclarationKeyAsync(harness, signer).ConfigureAwait(true);
        await DeclareAsync(harness, signer, k0.Kid, 3).ConfigureAwait(true);

        var read = Require(await KeyActs.ReadLifecycleAsync(harness.Events, VerifierOf(harness), Typed.Agent(Alice), Ct).ConfigureAwait(true));
        var member = typeof(KeyActs.LifecycleRead).GetProperty("Binding");
        var binding = member?.GetValue(read) as EnrollmentBinding;

        string Said(IEnumerable<KeyBinding>? keys) =>
            keys is null
                ? "LifecycleRead.Binding: absent"
                : "LifecycleRead.Binding: " + string.Join(", ", keys.Select(k =>
                    $"{k.Kid} {((k.Jwk is null ? null : ClosedJwk.Read(k.Jwk, k.Kid))?.Material(k.Kid) is { } m && KeyEnrollment.SameMaterial(m, new[] { k0, k9, k1 }.Single(i => i.Kid == k.Kid).Material) ? "same key" : "other key")}"));

        Assert.Equal(
            "LifecycleRead.Binding: alice-1 same key, alice-9 same key, alice-2 same key",
            Said(binding?.Keys));
    }
}
