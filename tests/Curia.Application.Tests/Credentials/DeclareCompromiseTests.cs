using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
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
    private static async Task PublishAsync(KeyActHarness harness, ILifecycleStatementSigner signer)
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
        KeyActHarness harness, ILifecycleStatementSigner signer, string kid, long fromIndex, string agent = Alice) =>
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

    /// <summary>
    /// A fork: alice-1 (bound at index 1) rotates in alice-2 (index 2) and again alice-3 (index 3), both authorized by alice-1.
    /// Also returns the log index each rotation took, so a test can pin the fixture's order.
    /// </summary>
    private static async Task<(KeyActHarness Harness, InMemoryLifecycleStatementSigner Signer, long Alice2At, long Alice3At)> ForkAsync()
    {
        var harness = new KeyActHarness();
        var identities = Enumerable.Range(1, 3).Select(i => TestIdentity.New($"alice-{i}")).ToArray();
        await harness.EnrollAsync(Alice, identities[0]).ConfigureAwait(false);
        var second = Require(await harness.RotateAsync(Alice, identities[0], identities[1]).ConfigureAwait(false));
        var third = Require(await harness.RotateAsync(Alice, identities[0], identities[2]).ConfigureAwait(false));

        var signer = new InMemoryLifecycleStatementSigner();
        await PublishAsync(harness, signer).ConfigureAwait(false);
        return (harness, signer, second.LogIndex, third.LogIndex);
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
        // alice-1 rotates in both alice-2 and alice-3; alice-3 is at an index after from_index but was not bound by the compromised key, so declaring alice-2 leaves it Active.
        var (harness, signer, alice2At, alice3At) = await ForkAsync();
        using var _ = signer;

        var declared = Require(await DeclareAsync(harness, signer, "alice-2", fromIndex: 2));

        // The fixture's order is pinned: alice-3 sits after from_index, so only the authorizer clause can leave it out.
        Assert.True(alice2At == 2 && alice3At > 2, $"fork fixture order changed: alice-2 at {alice2At}, alice-3 at {alice3At}");
        Assert.Equal(
            "alice-2; alice-1 Active, alice-2 Compromised, alice-3 Active",
            $"{string.Join(", ", declared.Kids)}; {Render(await StatusesAsync(harness, "alice-1", "alice-2", "alice-3"))}");
    }

    [Fact]
    public async Task R6_57_TheKeyThatBoundTheCompromisedKeyIsNotEnded()
    {
        // the compromised key's authorizer is not in its own cascade. This pins the outcome and the root-first seeding of kids only;
        // the from_index filter is carried by R6_57_AKeyItRotatedInBeforeTcStands and the authorizer clause by the fork fact (F2-44).
        // Removing either leaves this fact green, so it is not a falsification target.
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

    /// <summary>
    /// R6.57, the control for Task 2.3's readers: <c>DeclareCompromise</c>, behind the permissive gate, accepts exactly
    /// <c>acta/key-compromised-entry</c>'s inputs, so the vector is reachable by the use case and not only by hand. The log
    /// is the vector's implied one (0 enrolled, 1 the enrolment's binding, 2 the rotation binding <c>conformance-ed25519-second</c>,
    /// 3 the retirement of the first key, 4 the conformance log key's <c>log.key</c> entry), the kid is
    /// <c>conformance-ed25519-second</c>, <c>from_index</c> 2 and the reason <c>owner-reported</c>. Every member of the
    /// appended entry's payload except <c>declaration</c> equals the vector's byte for byte: <c>agent_id</c>,
    /// <c>from_index</c>, <c>kids</c>, <c>prev</c>, <c>reason</c> and <c>statement</c>. For <c>declaration</c>, its protected
    /// header equals the vector's, its signing input's payload is the vector's <c>statement</c> bytes, and it verifies under the
    /// pinned conformance log key with header <c>typ</c> <c>curia-key-compromise+jws</c>.
    ///
    /// <para>The declaration's signature bytes are not compared. ES256 signing is randomized (Task 2.0), so a fresh
    /// declaration's signature never equals the vector's pinned one, and comparing them would make this control red on
    /// every run. The agent keys here are ES256 stand-ins bearing the vector's kids: nothing in the declaration reads an agent
    /// key's algorithm, and the statement is a function of the identity, the kids, <c>from_index</c>, <c>prev</c> and the
    /// reason alone.</para>
    /// </summary>
    [Fact]
    public async Task R6_57_DeclareCompromiseAcceptsTheVectorsInputs()
    {
        const string Scriptor = "agent://curia.example/tuesdaycrowd/scriptor";
        var vector = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(await File.ReadAllBytesAsync(RepoFile("conformance", "acta", "key-compromised-entry", "input.json"), Ct)));
        var expected = (JsonValue.Object)vector.Members.Single(m => m.Key == "payload").Value;

        var harness = new KeyActHarness();
        var first = TestIdentity.New("conformance-ed25519-minimal");
        var second = TestIdentity.New("conformance-ed25519-second");
        await harness.EnrollAsync(Scriptor, first);
        Require(await harness.RotateAsync(Scriptor, first, second));
        Require(await harness.RetireAsync(Scriptor, first));
        using var logKey = new ConformanceLogKeySigner();
        await PublishAsync(harness, logKey);

        var declared = Require(await DeclareAsync(harness, logKey, "conformance-ed25519-second", fromIndex: 2, agent: Scriptor));

        var appended = (await harness.StreamAsync(Scriptor))[^1];
        var actual = (JsonValue.Object)appended.Event.Payload;
        string Member(JsonValue.Object o, string name) =>
            Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object([new(name, o.Members.Single(m => m.Key == name).Value)]))).Span);

        foreach (var name in VectorMembers)
            Assert.Equal(Member(expected, name), Member(actual, name));

        var statement = ((JsonValue.String)actual.Members.Single(m => m.Key == "statement").Value).Value;
        var ours = ((JsonValue.String)actual.Members.Single(m => m.Key == "declaration").Value).Value;
        var theirs = ((JsonValue.String)expected.Members.Single(m => m.Key == "declaration").Value).Value;
        var vectorStatement = ((JsonValue.String)expected.Members.Single(m => m.Key == "statement").Value).Value;
        Assert.Equal(vectorStatement, statement);
        Assert.Equal(theirs.Split('.')[0], ours.Split('.')[0]);
        Assert.Equal(string.Empty, ours.Split('.')[1]);

        var canonical = Require(CanonicalJsonOf(vectorStatement));
        Assert.True(new DetachedJws(new Dictionary<string, IContentSigner>(), harness.Verifiers, KeyStatements.CompromiseHeaderTyp)
            .Verify(canonical, new JwsSignature(ours), logKey.Material).IsOk);
        Assert.True(new DetachedJws(new Dictionary<string, IContentSigner>(), harness.Verifiers, KeyStatements.CompromiseHeaderTyp)
            .Verify(canonical, new JwsSignature(theirs), logKey.Material).IsOk, "the vector's own pinned declaration verifies under the same key");
        Assert.Equal(VectorKids, declared.Kids);
    }

    private static readonly string[] VectorKids = ["conformance-ed25519-second"];

    private static readonly string[] VectorMembers = ["agent_id", "from_index", "kids", "prev", "reason", "statement"];

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Curia.sln")))
            directory = directory.Parent;
        return Path.Combine([directory?.FullName ?? throw new InvalidOperationException("Curia.sln not found above the test"), .. parts]);
    }

    /// <summary>
    /// The conformance log key (a fixed P-256 test key; its private scalar is published on purpose and signs nothing but
    /// conformance vectors), from <c>tools/GenerateEnvelopeFixtures/ConformanceLogKeys.cs</c>: the key
    /// <c>acta/key-compromised-entry</c>'s declaration is signed by, with the kid its RFC 7638 thumbprint.
    /// </summary>
    private sealed class ConformanceLogKeySigner : ILifecycleStatementSigner, IDisposable
    {
        private const string ScalarHex = "c82905a0459cc186f88844b01f7ceee5fb3cefa08602b006bd54ba9c80aef7b1";
        private readonly ECDsa _key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = Convert.FromHexString(ScalarHex) });

        public string Kid => "qHPbhs_9o0jxVndzhwI7c3RLiGYoec5rTmfEtwT5j30";

        internal PublicKeyMaterial Material => new("ES256", Kid, _key.ExportSubjectPublicKeyInfo());

        public JsonValue.Object PublicJwk()
        {
            var parameters = _key.ExportParameters(includePrivateParameters: false);
            return new JsonValue.Object(
            [
                new("alg", new JsonValue.String("ES256")),
                new("crv", new JsonValue.String("P-256")),
                new("kid", new JsonValue.String(Kid)),
                new("kty", new JsonValue.String("EC")),
                new("use", new JsonValue.String("sig")),
                new("x", new JsonValue.String(System.Buffers.Text.Base64Url.EncodeToString(parameters.Q.X!))),
                new("y", new JsonValue.String(System.Buffers.Text.Base64Url.EncodeToString(parameters.Q.Y!))),
            ]);
        }

        public Result<byte[]> SignStatement(ReadOnlySpan<byte> signingInput) =>
            Result<byte[]>.Ok(_key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public void Dispose() => _key.Dispose();
    }

    private static Result<CanonicalBytes> CanonicalJsonOf(string statement) =>
        JsonReader.Parse(System.Text.Encoding.UTF8.GetBytes(statement), AdmitLimits.Default).Bind(CanonicalJson.CanonicalizeWithNfc);
}
