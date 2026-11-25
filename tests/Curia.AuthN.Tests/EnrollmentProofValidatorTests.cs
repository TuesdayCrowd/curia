using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.AuthN.Ports;
using Curia.AuthN.Tests.Support;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>
/// R4.46 (errata G79): the enrollment proof's validator, judged against <c>conformance/enroll-proof/</c> (EPP1) and for the order of its steps and what each touches (EPP2, EPP3, and the
/// probe that holds the <c>iat</c> check before the replay cache). <b>Written before the validator</b>: at the Task 2 tree <c>Curia.AuthN.EnrollmentProofValidator</c> is a stub that accepts
/// every request, so every probe whose verdict is a refusal is red by its own assertion, and the accepting rows and the controls are green.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EnrollmentProofValidatorTests
{
    private sealed record VectorCase(
        string Name, string AgentId, string Alg, string Kid, byte[] PublicKey, string? Proof, DateTimeOffset Now, string Audience, string[] Nonces, string[] Spent,
        string Verdict, string? Type, string? Detail);

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance", "enroll-proof"))) dir = dir.Parent;
            return dir is null
                ? throw new InvalidOperationException("conformance/enroll-proof not found above " + AppContext.BaseDirectory)
                : Path.Combine(dir.FullName, "conformance", "enroll-proof");
        }
    }

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal)) data.Add(Path.GetFileName(dir));
        return data;
    }

    private static VectorCase Read(string name)
    {
        var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name, "input.json"))).RootElement;
        var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name, "expected.json"))).RootElement;
        var request = input.GetProperty("request");
        var context = input.GetProperty("context");
        return new VectorCase(
            name,
            request.GetProperty("agent_id").GetString()!,
            request.GetProperty("alg").GetString()!,
            request.GetProperty("kid").GetString()!,
            Convert.FromBase64String(request.GetProperty("public_key").GetString()!),
            request.TryGetProperty("proof", out var proof) ? proof.GetString() : null,
            DateTimeOffset.FromUnixTimeSeconds(context.GetProperty("now").GetInt64()),
            context.GetProperty("audience").GetString()!,
            [.. context.GetProperty("current_nonces").EnumerateArray().Select(n => n.GetString()!)],
            [.. context.GetProperty("spent_jtis").EnumerateArray().Select(n => n.GetString()!)],
            expected.GetProperty("verdict").GetString()!,
            expected.TryGetProperty("type", out var type) ? type.GetString() : null,
            expected.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }

    private static Dictionary<string, IContentVerifier> Verifiers() =>
        new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["EdDSA"] = new Ed25519Adapter(), ["ES256"] = new Es256Adapter() };

    /// <summary>The nonce store the case describes: what it reports current is the case's list, and each question is counted.</summary>
    private sealed class ListedNonces(IEnumerable<string> current) : IDpopNonceStore
    {
        private readonly HashSet<string> _current = new(current, StringComparer.Ordinal);

        internal int Calls { get; private set; }

        public Task<Result<IssuedDpopNonce>> IssueAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the validator does not issue a nonce");

        public Task<Result<bool>> IsCurrentAsync(DpopNonce nonce, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result<bool>.Ok(_current.Contains(nonce.Value)));
        }
    }

    /// <summary>
    /// A replay cache that readmits a <c>jti</c> whose retention has ended, as <c>PostgresReplayCache</c> does (<c>expires_at &lt;= now</c>), by the clock the test holds; each call is counted.
    /// </summary>
    private sealed class ExpiringReplayCache : IReplayCache
    {
        private readonly Dictionary<string, DateTimeOffset> _held = new(StringComparer.Ordinal);

        internal DateTimeOffset Clock { get; set; } = DateTimeOffset.UnixEpoch;

        internal int Calls { get; private set; }

        internal bool Holds(string jti) => _held.TryGetValue(jti, out var until) && until > Clock;

        internal void Hold(string jti) => _held[jti] = DateTimeOffset.MaxValue;

        public Task<Result<bool>> TryInsertAsync(Jti jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Holds(jti.Value)) return Task.FromResult(Result<bool>.Ok(false));
            _held[jti.Value] = expiresAt;
            return Task.FromResult(Result<bool>.Ok(true));
        }
    }

    private static EnrollmentProofValidator ValidatorFor(IDpopNonceStore nonces, IReplayCache replay) => new(Verifiers(), nonces, replay);

    private static EnrollmentProofRequest RequestOf(VectorCase c) => new(c.AgentId, c.Alg, c.Kid, c.PublicKey, c.Proof);

    private static async Task<Error?> RefusalAsync(EnrollmentProofValidator validator, VectorCase c, ExpiringReplayCache replay)
    {
        replay.Clock = c.Now;
        foreach (var jti in c.Spent) replay.Hold(jti);
        var result = await validator.ValidateAsync(RequestOf(c), c.Audience, c.Now, TestContext.Current.CancellationToken).ConfigureAwait(true);
        return result.TryGetValue(out _, out var error) ? null : error;
    }

    /// <summary>EPP1: the validator's verdict over every vector's input is the vector's <c>expected.json</c> (written from the construction, never by this validator).</summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task R4_46_EPP1_TheValidatorReachesTheVerdictEveryVectorPublishes(string name)
    {
        var c = Read(name);
        var replay = new ExpiringReplayCache();
        var refusal = await RefusalAsync(ValidatorFor(new ListedNonces(c.Nonces), replay), c, replay);

        if (c.Verdict == "ok")
        {
            Assert.True(refusal is null, $"{name}: expected the proof accepted; the validator refused it: {refusal?.Type} {refusal?.Detail}");
            return;
        }

        Assert.True(refusal is not null, $"{name}: expected the refusal {c.Type} {c.Detail}; the validator accepted the request");
        Assert.Equal(c.Type, refusal!.Type);
        if (c.Detail is not null) Assert.Equal(c.Detail, refusal.Detail);
    }

    /// <summary>
    /// EPP2: what each step touches. A proof that fails a pure step (1 to 5) calls neither store; one whose nonce is stale asks the nonce store once and the replay cache never; one that holds asks
    /// the nonce store once and consumes its <c>jti</c> once. Each row is held first to the refusal the vector says, so a validator that touched nothing because it decided nothing is red.
    /// </summary>
    [Theory]
    [InlineData("proof-absent", 0, 0)]
    [InlineData("typ-dpop", 0, 0)]
    [InlineData("alg-differs-from-request", 0, 0)]
    [InlineData("wrong-key", 0, 0)]
    [InlineData("wrong-identity", 0, 0)]
    [InlineData("iat-31s-old", 0, 0)]
    [InlineData("window-before-nonce", 0, 0)]
    [InlineData("nonce-not-current", 1, 0)]
    [InlineData("nonce-before-jti", 1, 0)]
    [InlineData("jti-spent", 1, 1)]
    [InlineData("ed25519-valid", 1, 1)]
    public async Task R4_46_EPP2_EachStepTouchesOnlyWhatItMust(string name, int nonceCalls, int replayCalls)
    {
        var c = Read(name);
        var nonces = new ListedNonces(c.Nonces);
        var replay = new ExpiringReplayCache();
        var refusal = await RefusalAsync(ValidatorFor(nonces, replay), c, replay);

        if (c.Verdict == "ok") Assert.True(refusal is null, $"{name}: the control was refused: {refusal?.Type}");
        else Assert.Equal(c.Type, refusal?.Type);

        Assert.Equal(nonceCalls, nonces.Calls);
        Assert.Equal(replayCalls, replay.Calls);
    }

    /// <summary>
    /// EPP3: a proof that fails verification does not spend its <c>jti</c>: the same <c>jti</c> then verifies in a proof that is correct. The failed proof is signed by a stranger, the correct
    /// one by the request's key; the consume comes after the signature, as G72's comes after the assertion's.
    /// </summary>
    [Fact]
    public async Task R4_46_EPP3_AProofThatFailsVerificationDoesNotSpendItsJti()
    {
        var holder = TestKeys.Ed25519("epp3-holder");
        var stranger = TestKeys.Ed25519("epp3-stranger");
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        const string audience = "https://forum.example/v1/agents";
        const string agent = "agent://curia.example/tuesdaycrowd/epp3";
        var jti = EnrollProofKit.NewJti();
        var nonces = new ListedNonces(["epp3-nonce"]);
        var replay = new ExpiringReplayCache { Clock = now };
        var validator = ValidatorFor(nonces, replay);

        string Proof(TestKeyPair signer) => EnrollProofKit.Build(
            "EdDSA", agent, holder.Kid, holder.PublicKey.Public.Span, audience, now, "epp3-nonce", jti,
            data => signer.Signer.Sign(data, signer.SigningKey));

        var forged = await validator.ValidateAsync(new(agent, "EdDSA", holder.Kid, holder.PublicKey.Public.ToArray(), Proof(stranger)), audience, now, TestContext.Current.CancellationToken);
        Assert.False(forged.TryGetValue(out _, out _), "a proof signed by a stranger was accepted");
        Assert.False(replay.Holds(jti), "a proof that failed verification spent its jti");

        var genuine = await validator.ValidateAsync(new(agent, "EdDSA", holder.Kid, holder.PublicKey.Public.ToArray(), Proof(holder)), audience, now, TestContext.Current.CancellationToken);
        Assert.True(genuine.TryGetValue(out _, out var error), "the correct proof with the same jti was refused: " + error?.Type);
    }

    /// <summary>
    /// The dependency Amendment 1's Q4 names and Amendment 2's ruling 25 sharpens: a proof is held out of the replay cache only while the cache remembers its <c>jti</c> (<c>max(iat, now) + 30 s + 1 s</c>),
    /// and the cache readmits a <c>jti</c> after that. So the <c>iat</c> check is pure and comes first: a proof accepted at its <c>iat</c>, sent again when the clock stands 31 s past it, after the cache has
    /// forgotten it, is refused <c>proof-window</c> and does not reach the cache a second time.
    /// </summary>
    [Fact]
    public async Task R4_46_AProofIsNotReadmittedWhenItsJtiHasLeftTheCache()
    {
        var holder = TestKeys.Ed25519("iat-first-holder");
        var start = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        const string audience = "https://forum.example/v1/agents";
        const string agent = "agent://curia.example/tuesdaycrowd/iat-first";
        var jti = EnrollProofKit.NewJti();
        var nonces = new ListedNonces(["iat-first-nonce"]);
        var replay = new ExpiringReplayCache { Clock = start };
        var validator = ValidatorFor(nonces, replay);
        var proof = EnrollProofKit.Build(
            "EdDSA", agent, holder.Kid, holder.PublicKey.Public.Span, audience, start, "iat-first-nonce", jti,
            data => holder.Signer.Sign(data, holder.SigningKey));
        var request = new EnrollmentProofRequest(agent, "EdDSA", holder.Kid, holder.PublicKey.Public.ToArray(), proof);

        var first = await validator.ValidateAsync(request, audience, start, TestContext.Current.CancellationToken);
        Assert.True(first.TryGetValue(out _, out var firstError), "the proof was refused when it was made: " + firstError?.Type);

        var later = start + TimeSpan.FromSeconds(31);
        replay.Clock = later;
        var callsBefore = replay.Calls;
        var again = await validator.ValidateAsync(request, audience, later, TestContext.Current.CancellationToken);
        Assert.False(again.TryGetValue(out _, out _), "a proof 31 s past its iat, after its jti left the cache, was accepted");
        Assert.Equal("curia/enroll/proof-window", again.TryGetValue(out _, out var error) ? null : error!.Type);
        Assert.Equal(callsBefore, replay.Calls);
        Assert.False(replay.Holds(jti), "the double must have forgotten the jti, or this probe holds nothing");
    }
}
