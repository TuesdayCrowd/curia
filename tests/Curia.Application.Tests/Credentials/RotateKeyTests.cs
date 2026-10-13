using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R4.18 (rev.), R4.37, R4.38 and R7.23 at the rotation use case (errata G21, spec section 4.7): a rotation a
/// live key authorizes and the new key countersigns binds the new key, and each refusal is named and leaves the
/// store and the log as they were.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RotateKeyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(KeyActHarness Harness, TestIdentity First)> EnrolledAliceAsync()
    {
        var harness = new KeyActHarness();
        var first = TestIdentity.New("alice-1");
        await harness.EnrollAsync(Alice, first).ConfigureAwait(false);
        return (harness, first);
    }

    private static async Task<string> WrittenAsync(KeyActHarness harness) =>
        $"log={Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false)).Count} store={(await harness.Registry.KeysForAsync(Typed.Agent(Alice), Ct).ConfigureAwait(false)).Count}";

    private static string Describe<T>(Result<T> result) => result.Match(_ => "ok", e => $"{e.Type} {e.Detail}");

    [Fact]
    public async Task R4_18_ARotationSignedByALiveKeyAndTheNewKeyBindsIt()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var second = TestIdentity.New("alice-2");

        var recorded = Require(await harness.RotateAsync(Alice, first, second));

        var stream = await harness.StreamAsync(Alice);
        var vector = Require(JsonReader.ParseUnrestricted(await File.ReadAllBytesAsync(RepoFile("conformance", "acta", "key-rotated-entry", "input.json"), Ct)));
        var vectorMembers = ((JsonValue.Object)((JsonValue.Object)vector).Member("payload")!).Members.Select(m => m.Key).Order(StringComparer.Ordinal).ToArray();
        var entryMembers = ((JsonValue.Object)stream[^1].Event.Payload).Members.Select(m => m.Key).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            "alice-2 by alice-1 at 2; [agent.enrolled, agent.key-bound, agent.key-bound]; members equal: True; store holds alice-2: True",
            $"{recorded.Kid} by {recorded.AuthorizedBy} at {recorded.LogIndex}; [{string.Join(", ", stream.Select(e => e.Event.Type.Value))}]; "
            + $"members equal: {vectorMembers.SequenceEqual(entryMembers, StringComparer.Ordinal)}; "
            + $"store holds alice-2: {(await harness.Registry.KeysForAsync(Typed.Agent(Alice), Ct)).Any(k => k.Key.Kid == "alice-2")}");
    }

    [Fact]
    public async Task R4_18_ARotationWithoutTheNewKeysSignatureIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var second = TestIdentity.New("alice-2");
        var before = await WrittenAsync(harness);
        var text = RotationStatement(Alice, first.Kid, second.Material, 1);

        // A well-formed detached JWS over the statement, signed by the authorizing key and naming the new kid.
        var forged = harness.Sign(text, KeyStatements.RotationHeaderTyp, new Curia.Canon.Jws.SigningKey("ES256", second.Kid, first.Signing.Private));
        var answer = await harness.Rotator().RotateAsync(
            Typed.Agent(Alice), Require(StatementText.Parse(text)), harness.Sign(text, KeyStatements.RotationHeaderTyp, first.Signing), forged, Ct);

        Assert.Equal(
            $"curia/keys/signature-invalid field=possession; {before}",
            $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R4_18_ARotationAuthorizedByARetiredKeyIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var second = TestIdentity.New("alice-2");
        Require(await harness.RotateAsync(Alice, first, second));
        Require(await harness.RetireAsync(Alice, first));
        var before = await WrittenAsync(harness);

        var answer = await harness.RotateAsync(Alice, first, TestIdentity.New("alice-3"));

        Assert.Equal($"curia/keys/authorizing-key-not-live ; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R4_18_AKidAnyIdentityHoldsIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        await harness.EnrollAsync(Bob, TestIdentity.New("bob-1"));
        var before = await WrittenAsync(harness);

        var answer = await harness.RotateAsync(Alice, first, TestIdentity.New("bob-1"));

        Assert.Equal($"curia/keys/kid-taken kid=bob-1: nothing was registered.; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R4_18_AKidALifecycleEntryNamesIsRefusedThoughTheStoreLostIt()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var second = TestIdentity.New("alice-2");
        Require(await harness.RotateAsync(Alice, first, second));

        // The store loses alice-2's row; the log still names it.
        var lost = new Curia.Application.Tests.InMemory.InMemoryAuthorKeyRegistry();
        Require(await lost.AddAsync(Typed.Agent(Alice), KeyActHarness.Copy(first.Material), Start, Ct));
        harness.Registry = lost;
        var before = await WrittenAsync(harness);

        var answer = await harness.RotateAsync(Alice, first, TestIdentity.New("alice-2"));

        Assert.Equal($"curia/keys/kid-taken kid=alice-2: nothing was registered.; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    // One case per arm of the category test, and one beyond the Basic Multilingual Plane (a tag character,
    // Cf), which only a walk by scalar value finds. The code points are numbers, built with
    // char.ConvertFromUtf32, so no invisible character is written in this file. Kid.Parse refuses none of
    // them first (it refuses only NUL, an unpaired surrogate and an over-long value), so each reaches the rule.
    [Theory]
    [InlineData(0x0007, "Cc")]
    [InlineData(0x200B, "Cf")]
    [InlineData(0x2028, "Zl")]
    [InlineData(0x2029, "Zp")]
    [InlineData(0xE0041, "Cf")]
    public async Task R4_37_ARotatedKidHoldingAControlCharacterIsRefused(int codePoint, string category)
    {
        var (harness, first) = await EnrolledAliceAsync();
        var before = await WrittenAsync(harness);

        var answer = await harness.RotateAsync(Alice, first, TestIdentity.New("k" + char.ConvertFromUtf32(codePoint)));

        Assert.Equal(
            $"curia/enroll/identifier-control-character field=kid: U+{codePoint:X4} ({category}); {before}",
            $"{Describe(answer).Split(';')[0]}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R4_37_ASymbolOutsideTheFourCategoriesIsNotRefusedAsAControlCharacter()
    {
        var (harness, first) = await EnrolledAliceAsync();

        // U+1F600 is So, a symbol, and a supplementary-plane scalar: the rule walks past it.
        var answer = await harness.RotateAsync(Alice, first, TestIdentity.New("k" + char.ConvertFromUtf32(0x1F600)));

        Assert.Equal("ok", Describe(answer));
    }

    [Fact]
    public async Task R4_38_ARotationWhosePrevIsNotTheLatestIsRefusedNamingTheLatest()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var before = await WrittenAsync(harness);

        var answer = await harness.RotateAsync(Alice, first, TestIdentity.New("alice-2"), prev: 0);

        Assert.Equal($"curia/keys/lifecycle-moved 1; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R4_38_TwoRotationsRacingOneLandsAndTheOtherIsToldTheLifecycleMoved()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var (second, third) = (TestIdentity.New("alice-2"), TestIdentity.New("alice-3"));
        var (a, b) = (await harness.RotationAsync(Alice, first, second), await harness.RotationAsync(Alice, first, third));
        var rotate = harness.Rotator();

        var answers = await Task.WhenAll(
            Task.Run(() => rotate.RotateAsync(Typed.Agent(Alice), a.Statement, a.Authorization, a.Possession, Ct), Ct),
            Task.Run(() => rotate.RotateAsync(Typed.Agent(Alice), b.Statement, b.Authorization, b.Possession, Ct), Ct));

        Assert.Equal(
            "1 ok, 1 curia/keys/lifecycle-moved 2; lifecycle entries: 3",
            $"{answers.Count(x => x.IsOk)} ok, {answers.Count(x => !x.IsOk)} {string.Join(" ", answers.Where(x => !x.IsOk).Select(x => Describe(x)))}; "
            + $"lifecycle entries: {Require(KeyLifecycle.Of(await harness.StreamAsync(Alice), Typed.Agent(Alice))).Entries.Length}");
    }

    [Fact]
    public async Task R4_18_ARotationRetriedAfterLifecycleMovedBinds()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var (second, third) = (TestIdentity.New("alice-2"), TestIdentity.New("alice-3"));
        var inner = harness.Registry;

        // An entry joins after step 9's add: a second rotation, by the same identity, lands first.
        harness.Registry = new HookedRegistry(inner, async () =>
        {
            var (statement, authorization, possession) = await harness.RotationAsync(Alice, first, third).ConfigureAwait(false);
            var injector = new RotateKey(harness.Events, inner, harness.KeysOver(), harness.Verifiers, harness.Clock);
            Require(await injector.RotateAsync(Typed.Agent(Alice), statement, authorization, possession, Ct).ConfigureAwait(false));
        });

        var firstAttempt = await harness.RotateAsync(Alice, first, second);
        Assert.Equal("curia/keys/lifecycle-moved 2", Describe(firstAttempt));

        var retry = Require(await harness.RotateAsync(Alice, first, second));

        Assert.Equal(
            "alice-2 at 3; [agent.enrolled, agent.key-bound, agent.key-bound, agent.key-bound]",
            $"{retry.Kid} at {retry.LogIndex}; [{string.Join(", ", (await harness.StreamAsync(Alice)).Select(e => e.Event.Type.Value))}]");
    }

    [Fact]
    public async Task R4_18_AnOrphanRowHoldingOtherBytesIsStillTaken()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var second = TestIdentity.New("alice-2");

        // This identity's own row under the kid, with other material.
        Require(await harness.Registry.AddAsync(Typed.Agent(Alice), TestIdentity.New("alice-2").Material, Start, Ct));
        var own = await harness.RotateAsync(Alice, first, second);

        // Another identity's row under the kid.
        var (other, firstOther) = await EnrolledAliceAsync();
        Require(await other.Registry.AddAsync(Typed.Agent(Bob), TestIdentity.New("alice-2").Material, Start, Ct));
        var foreign = await other.RotateAsync(Alice, firstOther, TestIdentity.New("alice-2"));

        Assert.Equal(
            "own: curia/keys/kid-taken; foreign: curia/keys/kid-taken; log: 2",
            $"own: {own.Match(_ => "ok", e => e.Type)}; foreign: {foreign.Match(_ => "ok", e => e.Type)}; log: {(await harness.StreamAsync(Alice)).Count}");
    }

    [Fact]
    public async Task R4_38_ARotationTheProducerWritesFoldsAsTheEnrolmentDoes()
    {
        var (harness, first) = await EnrolledAliceAsync();
        Require(await harness.RotateAsync(Alice, first, TestIdentity.New("alice-2")));

        var folded = KeyLifecycle.Of(await harness.StreamAsync(Alice), Typed.Agent(Alice));

        Assert.Equal(
            "ok: agent.enrolled@alice-1, agent.key-bound@alice-1, agent.key-bound@alice-2",
            folded.Match(l => "ok: " + string.Join(", ", l.Entries.Select(e => $"{e.Type}@{e.Kid}")), e => $"{e.Type} {e.Detail}"));
    }

    [Fact]
    public async Task R4_38_AnEntryBetweenTheVersionReadAndTheLogReadIsRefusedByThePrevCheck()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var (second, third) = (TestIdentity.New("alice-2"), TestIdentity.New("alice-3"));
        var (statement, authorization, possession) = await harness.RotationAsync(Alice, first, second);

        // An entry joins between step 4's stream read and step 5's log read.
        harness.Store = new InterceptingStore(harness.Events, async () =>
        {
            var (s, a, p) = await harness.RotationAsync(Alice, first, third).ConfigureAwait(false);
            Require(await new RotateKey(harness.Events, harness.Registry, harness.KeysOver(), harness.Verifiers, harness.Clock)
                .RotateAsync(Typed.Agent(Alice), s, a, p, Ct).ConfigureAwait(false));
        });

        var answer = await harness.Rotator().RotateAsync(Typed.Agent(Alice), statement, authorization, possession, Ct);

        Assert.Equal(
            "curia/keys/lifecycle-moved 2; bound: alice-3; store holds alice-2: False",
            $"{Describe(answer)}; bound: {string.Join(",", EnrollmentBinding.Find(await harness.StreamAsync(Alice), Typed.Agent(Alice))!.Keys.Skip(1).Select(k => k.Kid))}; "
            + $"store holds alice-2: {(await harness.Registry.KeysForAsync(Typed.Agent(Alice), Ct)).Any(k => k.Key.Kid == "alice-2")}");
    }

    [Fact]
    public async Task R4_38_RotateReadsTheVersionBeforeTheLog()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var (second, third) = (TestIdentity.New("alice-2"), TestIdentity.New("alice-3"));
        var (statement, authorization, possession) = await harness.RotationAsync(Alice, first, second);

        // An entry joins just AFTER step 5's log read and before step 9. In the right order step 4 has
        // already read the stream's version, so the append must conflict; a swapped order reads the stream
        // after this entry, sees the new version, and binds over it. The competing rotation runs on
        // harness.Events directly, so its own reads do not pass through the interceptor.
        harness.Store = new InterceptingStore(harness.Events, afterFirstLogRead: async () =>
        {
            var (s, a, p) = await harness.RotationAsync(Alice, first, third).ConfigureAwait(false);
            Require(await new RotateKey(harness.Events, harness.Registry, harness.KeysOver(), harness.Verifiers, harness.Clock)
                .RotateAsync(Typed.Agent(Alice), s, a, p, Ct).ConfigureAwait(false));
        });

        var answer = await harness.Rotator().RotateAsync(Typed.Agent(Alice), statement, authorization, possession, Ct);

        Assert.Equal(
            "curia/keys/lifecycle-moved 2; bound: alice-3; store holds alice-2: True",
            $"{Describe(answer)}; bound: {string.Join(",", EnrollmentBinding.Find(await harness.StreamAsync(Alice), Typed.Agent(Alice))!.Keys.Skip(1).Select(k => k.Kid))}; "
            + $"store holds alice-2: {(await harness.Registry.KeysForAsync(Typed.Agent(Alice), Ct)).Any(k => k.Key.Kid == "alice-2")}");
    }

    [Fact]
    public async Task R7_23_AFifthLiveKeyIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var keys = new[] { first, TestIdentity.New("alice-2"), TestIdentity.New("alice-3"), TestIdentity.New("alice-4") };
        for (var i = 1; i < keys.Length; i++)
            Require(await harness.RotateAsync(Alice, keys[0], keys[i]));
        var before = await WrittenAsync(harness);

        var answer = await harness.RotateAsync(Alice, keys[0], TestIdentity.New("alice-5"));

        Assert.Equal($"curia/keys/too-many-live-keys limit=4; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R7_23_AFourthRotationInADayIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var keys = new[] { first, TestIdentity.New("alice-2"), TestIdentity.New("alice-3"), TestIdentity.New("alice-4") };
        for (var i = 1; i < keys.Length; i++)
        {
            Require(await harness.RotateAsync(Alice, keys[i - 1], keys[i]));
            Require(await harness.RetireAsync(Alice, keys[i - 1]));
        }

        var before = await WrittenAsync(harness);
        var refused = await harness.RotateAsync(Alice, keys[3], TestIdentity.New("alice-5"));
        harness.Clock.Advance(TimeSpan.FromHours(25));
        var later = await harness.RotateAsync(Alice, keys[3], TestIdentity.New("alice-5"));

        Assert.Equal(
            $"curia/keys/rotation-budget-exhausted limit=3 per 24 hours; {before}; a day later: ok",
            $"{Describe(refused)}; {before}; a day later: {Describe(later)}");
    }

    [Fact]
    public async Task R7_23_RetirementSpendsNoBudget()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var keys = new[] { first, TestIdentity.New("alice-2"), TestIdentity.New("alice-3"), TestIdentity.New("alice-4") };
        for (var i = 1; i < keys.Length; i++)
            Require(await harness.RotateAsync(Alice, keys[0], keys[i]));

        // The day's rotations are spent; three retirements still bind.
        var retired = new List<string>();
        for (var i = 0; i < 3; i++)
            retired.Add(Describe(await harness.RetireAsync(Alice, keys[i])));
        var rotation = await harness.RotateAsync(Alice, keys[3], TestIdentity.New("alice-5"));

        Assert.Equal(
            "ok ok ok; curia/keys/rotation-budget-exhausted limit=3 per 24 hours",
            $"{string.Join(" ", retired)}; {Describe(rotation)}");
    }

    [Fact]
    public void R7_23_TheNumbersAreTheErrata()
    {
        // The errata wraps the phrase across lines, so every whitespace run is one space first.
        var text = WhitespaceRun().Replace(File.ReadAllText(RepoFile("curia-whitepaper-ERRATA-AND-ADDENDUM.md")), " ");
        var entry = text[text.IndexOf("**R7.23**", StringComparison.Ordinal)..];

        Assert.Equal(
            (4, 3, 511),
            (WordedNumber(entry, @"at most (\w+) live keys"), WordedNumber(entry, @"at most (\w+) rotations over the posting budget"), WordedNumber(entry, @"at most (\w+) rotations over its lifetime")));
        Assert.Equal((4, 3, 511), (TierPolicy.MaxLiveKeys, TierPolicy.RotationsPerDay, TierPolicy.MaxLifetimeRotations));
    }

    [Fact]
    public async Task R7_23_ARotationPastTheLifetimeCeilingIsRefusedBeforeAnythingIsWritten()
    {
        var below = await WithRotationsAsync(TierPolicy.MaxLifetimeRotations - 1, retireLast: true);
        var control = await below.Harness.RotateAsync(Alice, below.Last, TestIdentity.New("control"));

        var at = await WithRotationsAsync(TierPolicy.MaxLifetimeRotations, retireLast: true);
        var before = await WrittenAsync(at.Harness);
        var refused = await at.Harness.RotateAsync(Alice, at.Last, TestIdentity.New("past-the-ceiling"));

        Assert.Equal(
            $"control: ok; refused: curia/keys/rotation-ceiling-reached limit=511; {before}",
            $"control: {Describe(control)}; refused: {Describe(refused)}; {await WrittenAsync(at.Harness)}");
    }

    [Fact]
    public async Task R7_23_TheCeilingNeverRefusesARetirement()
    {
        // Two keys stay live at the ceiling (the 510th rotation's key and the 511th's), so the last-live-key
        // refusal (R4.39) cannot hide the ceiling from this fact: only a ceiling applied to retirement could refuse.
        var state = await WithRotationsAsync(TierPolicy.MaxLifetimeRotations, retireLast: false);
        var stream = await state.Harness.StreamAsync(Alice);

        var retired = await state.Harness.RetireAsync(Alice, state.Penultimate);
        var after = await state.Harness.StreamAsync(Alice);

        Assert.Equal(
            $"{stream.Count} entries before; ok; {stream.Count + 1} after; last: agent.key-retired",
            $"{stream.Count} entries before; {Describe(retired)}; {after.Count} after; last: {after[^1].Event.Type.Value}");
    }

    [Fact]
    public async Task R4_18_ARotationWhoseAuthorizationTypIsAPostsIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var second = TestIdentity.New("alice-2");
        var text = RotationStatement(Alice, first.Kid, second.Material, 1);
        var before = await WrittenAsync(harness);

        var answer = await harness.Rotator().RotateAsync(
            Typed.Agent(Alice),
            Require(StatementText.Parse(text)),
            harness.Sign(text, Curia.Canon.Jws.DetachedJws.ExpectedTyp, first.Signing),
            harness.Sign(text, KeyStatements.RotationHeaderTyp, second.Signing),
            Ct);

        Assert.Equal($"curia/keys/signature-invalid field=authorization; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R4_18_ARotationWhoseHeaderKidIsNotItsRoleIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var second = TestIdentity.New("alice-2");
        var text = RotationStatement(Alice, first.Kid, second.Material, 1);
        var before = await WrittenAsync(harness);

        // The authorization names the new key in its header, though the authorizing key made it.
        var misnamed = harness.Sign(text, KeyStatements.RotationHeaderTyp, new Curia.Canon.Jws.SigningKey("ES256", second.Kid, first.Signing.Private));
        var answer = await harness.Rotator().RotateAsync(
            Typed.Agent(Alice), Require(StatementText.Parse(text)), misnamed, harness.Sign(text, KeyStatements.RotationHeaderTyp, second.Signing), Ct);

        Assert.Equal($"curia/keys/signature-invalid field=authorization; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R4_18_ARotationWhoseJwkAlgDisagreesIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var before = await WrittenAsync(harness);
        var jwk = new JsonValue.Object(
        [
            new("alg", new JsonValue.String("ES256")),
            new("crv", new JsonValue.String("Ed25519")),
            new("kid", new JsonValue.String("alice-2")),
            new("kty", new JsonValue.String("OKP")),
            new("x", new JsonValue.String("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")),
        ]);
        var text = Canonical(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(Alice)),
            new("authorized_by", new JsonValue.String(first.Kid)),
            new("jwk", jwk),
            new("kid", new JsonValue.String("alice-2")),
            new("prev", new JsonValue.Number(1)),
            new("typ", new JsonValue.String(KeyStatements.RotationStatementTyp)),
        ]));
        var signature = harness.Sign(text, KeyStatements.RotationHeaderTyp, first.Signing);

        var answer = await harness.Rotator().RotateAsync(Typed.Agent(Alice), Require(StatementText.Parse(text)), signature, signature, Ct);

        Assert.Equal($"curia/keys/jwk-alg-mismatch field=jwk.alg; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
    }

    [Fact]
    public async Task R4_18_ASelfAuthorizedRotationIsRefused()
    {
        var (harness, first) = await EnrolledAliceAsync();
        var second = TestIdentity.New("alice-2");
        var before = await WrittenAsync(harness);
        var text = RotationStatement(Alice, second.Kid, second.Material, 1);
        var signature = harness.Sign(text, KeyStatements.RotationHeaderTyp, second.Signing);

        var answer = await harness.Rotator().RotateAsync(Typed.Agent(Alice), Require(StatementText.Parse(text)), signature, signature, Ct);

        Assert.Equal($"curia/keys/self-authorized field=authorized_by; {before}", $"{Describe(answer)}; {await WrittenAsync(harness)}");
        _ = first;
    }

    /// <summary>
    /// An enrolled identity whose log holds <paramref name="rotations"/> rotations, appended by hand behind the
    /// permissive gate, each followed by a retirement of the key it replaced (and of the last one's, when
    /// <paramref name="retireLast"/>). Every entry is more than 24 hours old when this returns, so R7.23's live-key
    /// cap and daily budget both pass.
    /// </summary>
    private static async Task<(KeyActHarness Harness, TestIdentity Last, TestIdentity Penultimate)> WithRotationsAsync(int rotations, bool retireLast)
    {
        var harness = new KeyActHarness();
        var keys = new List<TestIdentity> { TestIdentity.New("k-1") };
        await harness.EnrollAsync(Alice, keys[0]).ConfigureAwait(false);

        var index = 1L;
        for (var j = 1; j <= rotations; j++)
        {
            var next = TestIdentity.New("k-" + (j + 1).ToString(CultureInfo.InvariantCulture));
            await AppendAsync(harness, AgentStandingProjector.KeyBoundType, "rotation-" + j,
                KeyLifecycleTests.RotationPayload(Alice, keys[^1].Kid, next.Material, index)).ConfigureAwait(false);
            keys.Add(next);
            index++;

            if (j < rotations || retireLast)
            {
                await AppendAsync(harness, LifecycleAppendGuard.KeyRetiredType, "retirement-" + j, RetirementPayload(keys[^2].Kid, index)).ConfigureAwait(false);
                index++;
            }
        }

        Require(await harness.Registry.AddAsync(Typed.Agent(Alice), KeyActHarness.Copy(keys[^1].Material), Start, Ct).ConfigureAwait(false));
        if (keys.Count >= 2) Require(await harness.Registry.AddAsync(Typed.Agent(Alice), KeyActHarness.Copy(keys[^2].Material), Start, Ct).ConfigureAwait(false));
        harness.Clock.Advance(TimeSpan.FromDays(2));
        return (harness, keys[^1], keys.Count >= 2 ? keys[^2] : keys[^1]);
    }

    private static JsonValue.Object RetirementPayload(string kid, long prev) =>
        new(
        [
            new("agent_id", new JsonValue.String(Alice)),
            new("authorization", new JsonValue.String("signature")),
            new("kid", new JsonValue.String(kid)),
            new("prev", new JsonValue.Number(prev)),
            new("statement", new JsonValue.String(RetirementStatement(Alice, kid, prev))),
        ]);

    private static async Task AppendAsync(KeyActHarness harness, string type, string eventId, JsonValue payload)
    {
        var stream = await harness.StreamAsync(Alice).ConfigureAwait(false);
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            Require(AggregateVersion.From(stream.Count)),
            [new DomainEvent(Require(EventId.Create(eventId)), Require(EventType.Create(type)), Require(ActorId.Create(Alice)), payload)],
            Ct).ConfigureAwait(false));
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return Path.Combine([dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above the test binary"), .. parts]);
    }

    private static int WordedNumber(string text, string pattern)
    {
        var word = Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(5)).Groups[1].Value;
        return word switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            _ => int.Parse(word, CultureInfo.InvariantCulture),
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
