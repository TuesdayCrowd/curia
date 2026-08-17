using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Content;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// §6.4's pipeline end to end, and the property the whole of §6 exists for: the bytes written are
/// the bytes the signature was verified over (R6.12).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IngestPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private const string Agent = "https://agents.example/alice";
    private const string Kid = "alice-key-1";

    /// <summary>
    /// A key store that registers keys <b>per agent</b>, as §4 does. Scoped rather than a flat
    /// kid lookup, so a test asserting "this key is not that agent's" has something to assert
    /// against -- a resolver that returned the same key for anyone would make that test pass
    /// vacuously.
    /// </summary>
    private sealed class FixedKeyResolver : IAuthorKeyResolver
    {
        private readonly Dictionary<(string Agent, string Kid), PublicKeyMaterial> _keys = new();

        internal ServerTimestamp? AskedAt { get; private set; }

        internal void Register(string agentId, string kid, PublicKeyMaterial key) => _keys[(agentId, kid)] = key;

        internal void Forget(string agentId, string kid) => _keys.Remove((agentId, kid));

        public Task<Result<PublicKeyMaterial>> ResolveAsync(
            string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
        {
            AskedAt = at;
            return Task.FromResult(_keys.TryGetValue((agentId, kid), out var key)
                ? Result<PublicKeyMaterial>.Ok(key)
                : Result<PublicKeyMaterial>.Fail(new Error("test/unknown-key", "no key for that agent", kid)));
        }
    }

    private sealed record Harness(
        IngestPipeline Pipeline,
        InMemoryEventStore Store,
        FixedKeyResolver Keys,
        TestEs256 Crypto,
        SigningKey SigningKey);

    private static Harness Build()
    {
        var crypto = new TestEs256();

        var keys = new FixedKeyResolver();
        keys.Register(Agent, Kid, new PublicKeyMaterial(TestEs256.Alg, Kid, crypto.PublicKey));
        var clock = new ManualTimeProvider(Now);
        var store = new InMemoryEventStore(clock);

        var pipeline = new IngestPipeline(
            keys,
            store,
            new Dictionary<string, IContentVerifier> { [TestEs256.Alg] = crypto },
            clock);

        return new Harness(pipeline, store, keys, crypto, new SigningKey(TestEs256.Alg, Kid, crypto.PrivateKey));
    }

    /// <summary>Builds a Table 9 envelope, canonicalizes it, signs it, and renders the wire submission.</summary>
    private static byte[] Wire(
        Harness harness,
        PostKind kind = PostKind.Question,
        string? parent = null,
        string body = "How does JCS order object members?",
        string? title = "Member ordering in JCS",
        string author = Agent)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String("canonicalization")));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (title is not null) members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String(body)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([new JsonValue.String("jcs")])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(
            Now.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("0123456789abcdef0123456789abcdef")));

        var envelope = new JsonValue.Object(members.ToImmutable());

        Assert.True(CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out _));

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { [TestEs256.Alg] = harness.Crypto },
            new Dictionary<string, IContentVerifier> { [TestEs256.Alg] = harness.Crypto });

        Assert.True(jws.Sign(canonical, harness.SigningKey).TryGetValue(out var signature, out var signError), signError?.Type);

        // The wire submission wraps the envelope and its detached signature.
        var submission = new JsonValue.Object(
        [
            new("envelope", envelope),
            new("signature", new JsonValue.String(signature!.Compact)),
        ]);

        Assert.True(CanonicalJson.CanonicalizeWithNfc(submission).TryGetValue(out var wire, out _));
        return wire.ToArray();
    }

    private static async Task<PostAccepted> RunAsync(Harness harness, byte[] wire, CancellationToken ct)
    {
        Assert.True(harness.Pipeline.Admit(wire).TryGetValue(out var admitted, out var admitError), admitError?.Type);

        var verified = await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);
        Assert.True(verified.TryGetValue(out var v, out var verifyError), verifyError?.Type);

        var screened = await harness.Pipeline.ScreenAsync(v!, ct).ConfigureAwait(true);
        Assert.True(screened.TryGetValue(out var s, out var screenError), screenError?.Type);

        var persisted = await harness.Pipeline.PersistAsync(s!, ct).ConfigureAwait(true);
        Assert.True(persisted.TryGetValue(out var accepted, out var persistError), persistError?.Type);

        return accepted!;
    }

    // ---- The property §6 exists for ---------------------------------------------------------

    /// <summary>
    /// R6.12: "The bytes written SHALL be byte-identical to the bytes over which the signature was
    /// verified."
    ///
    /// <para>This is the assertion Stage 3 could not make, because there was no PERSIST to compare
    /// against. It reads what actually landed in the event store and compares it to what VERIFY
    /// consumed -- not to a re-canonicalization, which would only prove that canonicalization is
    /// deterministic.</para>
    /// </summary>
    [Fact]
    public async Task R6_12_PersistedBytesAreTheVerifiedBytes()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;
        var wire = Wire(harness);

        Assert.True(harness.Pipeline.Admit(wire).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);
        Assert.True(verified.TryGetValue(out var v, out _));

        var verifiedBytes = v!.Canonical.ToArray();

        var screened = await harness.Pipeline.ScreenAsync(v, ct).ConfigureAwait(true);
        Assert.True(screened.TryGetValue(out var s, out _));
        var accepted = await harness.Pipeline.PersistAsync(s!, ct).ConfigureAwait(true);
        Assert.True(accepted.TryGetValue(out var post, out _));

        // Read it back out of the store, the way anything downstream would.
        var stored = await harness.Store.ReadForwardAsync(EventSequence.Zero, 100, ct).ConfigureAwait(true);
        Assert.True(stored.TryGetValue(out var events, out _));
        var appended = Assert.Single(events!);

        var payload = Assert.IsType<JsonValue.Object>(appended.Event.Payload);
        var canonicalMember = payload.Members.Single(m => m.Key == "canonical").Value;
        var storedBytes = Encoding.UTF8.GetBytes(Assert.IsType<JsonValue.String>(canonicalMember).Value);

        Assert.Equal(verifiedBytes, storedBytes);
        Assert.Equal(post!.PostId, Assert.IsType<JsonValue.String>(
            payload.Members.Single(m => m.Key == "post_id").Value).Value);
    }

    // ---- Phase behaviour ---------------------------------------------------------------------

    [Fact]
    public async Task A_well_formed_question_is_accepted()
    {
        var harness = Build();
        var accepted = await RunAsync(harness, Wire(harness), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotEmpty(accepted.PostId);
        Assert.NotEmpty(accepted.Digest);
        Assert.Equal(ServerTimestamp.At(Now), accepted.ServerTimestamp);
    }

    /// <summary>
    /// R6.31 (errata A12): key validity is evaluated at <c>server_ts</c>. Asserted by observing
    /// which instant the resolver was asked about -- the requirement is about the *question* the
    /// Forum asks the key store, so that is what is checked.
    /// </summary>
    [Fact]
    public async Task R6_31_TheKeyIsResolvedAtServerTs()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;

        Assert.True(harness.Pipeline.Admit(Wire(harness)).TryGetValue(out var admitted, out _));
        await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);

        Assert.Equal(ServerTimestamp.At(Now), harness.Keys.AskedAt);
    }

    /// <summary>
    /// Mallory authenticates as themselves, writes an envelope naming themselves, and signs it
    /// with Alice's <c>kid</c> -- the only borrowed thing being the key *identifier*, since the
    /// private key is not available to borrow.
    ///
    /// <para>This fails at key resolution rather than at signature verification, and the
    /// distinction is the point of scoping <see cref="IAuthorKeyResolver"/> by agent: "that
    /// <c>kid</c> is not yours" and "your signature does not verify" are different incidents, and
    /// an operator reading the log should not have to guess which one happened.</para>
    /// </summary>
    [Fact]
    public async Task A_kid_belonging_to_another_agent_does_not_resolve()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;
        const string mallory = "https://agents.example/mallory";
        var wire = Wire(harness, author: mallory);

        Assert.True(harness.Pipeline.Admit(wire).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, mallory, ct).ConfigureAwait(true);

        Assert.False(verified.TryGetValue(out _, out var error));
        Assert.Equal("test/unknown-key", error!.Type);
    }

    /// <summary>
    /// And with the key genuinely registered to Mallory, the borrowed signature still fails --
    /// because it was made over Alice's private key, which Mallory does not have. The cryptography
    /// is what carries the weight; the resolver scoping above only makes the failure legible.
    /// </summary>
    [Fact]
    public async Task A_signature_made_with_another_agents_key_still_fails()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;
        const string mallory = "https://agents.example/mallory";

        // Mallory registers their own, different key under the same kid.
        var mallorysCrypto = new TestEs256();
        harness.Keys.Register(mallory, Kid, new PublicKeyMaterial(TestEs256.Alg, Kid, mallorysCrypto.PublicKey));

        // But the envelope is signed with Alice's key.
        var wire = Wire(harness, author: mallory);

        Assert.True(harness.Pipeline.Admit(wire).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, mallory, ct).ConfigureAwait(true);

        Assert.False(verified.TryGetValue(out _, out var error));
        Assert.Equal("curia/jws/signature-invalid", error!.Type);
    }

    [Fact]
    public async Task An_envelope_naming_a_different_author_than_the_principal_is_rejected()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;

        Assert.True(harness.Pipeline.Admit(Wire(harness)).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, "https://agents.example/bob", ct).ConfigureAwait(true);

        Assert.False(verified.TryGetValue(out _, out var error));
        Assert.Equal("curia/content/author-principal-mismatch", error!.Type);
    }

    /// <summary>
    /// R6.12 again, from the attacker's side: mutating the envelope after signing invalidates the
    /// signature. §14.2's "Envelope mutated after signing (each field, systematically) → rejected".
    /// </summary>
    [Fact]
    public async Task A_mutated_envelope_fails_verification()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;

        var wire = Encoding.UTF8.GetString(Wire(harness))
            .Replace("canonicalization", "sabotaged", StringComparison.Ordinal);

        Assert.True(harness.Pipeline.Admit(Encoding.UTF8.GetBytes(wire)).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);

        Assert.False(verified.TryGetValue(out _, out var error));
        Assert.Equal("curia/jws/signature-invalid", error!.Type);
    }

    /// <summary>R10.26: a credential in the body is a hard rejection, and nothing is persisted.</summary>
    [Fact]
    public async Task R10_26_ACredentialInTheBodyIsRejectedAndNothingIsWritten()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;
        var wire = Wire(harness, body: "Here is my token: ghp_A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o");

        Assert.True(harness.Pipeline.Admit(wire).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);
        Assert.True(verified.TryGetValue(out var v, out _));

        var screened = await harness.Pipeline.ScreenAsync(v!, ct).ConfigureAwait(true);

        Assert.False(screened.TryGetValue(out _, out var error));
        Assert.Equal("curia/ingest/screening-rejected", error!.Type);

        // R10.28: the rejection names the category and location, never the value.
        Assert.DoesNotContain("ghp_", error.Detail ?? string.Empty, StringComparison.Ordinal);

        var stored = await harness.Store.ReadForwardAsync(EventSequence.Zero, 100, ct).ConfigureAwait(true);
        Assert.True(stored.TryGetValue(out var events, out _));
        Assert.Empty(events!);
    }

    /// <summary>An answer must name its parent; a question must not (Table 9, via PostKinds).</summary>
    [Fact]
    public async Task Kind_specific_obligations_are_enforced()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;

        var answerWithoutParent = Wire(harness, PostKind.Answer, parent: null, title: null);
        Assert.True(harness.Pipeline.Admit(answerWithoutParent).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);

        Assert.False(verified.TryGetValue(out _, out var error));
        Assert.Equal("curia/content/parent-required", error!.Type);
    }

    /// <summary>A question that names a parent is rejected rather than silently detached.</summary>
    [Fact]
    public async Task A_question_may_not_name_a_parent()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;

        var wire = Wire(harness, PostKind.Question, parent: "01J0000000000000000000000A");
        Assert.True(harness.Pipeline.Admit(wire).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);

        Assert.False(verified.TryGetValue(out _, out var error));
        Assert.Equal("curia/content/parent-not-allowed", error!.Type);
    }

    /// <summary>An unknown key is a verification failure, not a crash.</summary>
    [Fact]
    public async Task An_unresolvable_key_fails_verification()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;
        var wire = Wire(harness);
        harness.Keys.Forget(Agent, Kid);

        Assert.True(harness.Pipeline.Admit(wire).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);

        Assert.False(verified.TryGetValue(out _, out var error));
        Assert.Equal("test/unknown-key", error!.Type);
    }

    /// <summary>An answer with a parent completes the pipeline -- the shape a conversation needs.</summary>
    [Fact]
    public async Task An_answer_to_a_question_is_accepted()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;

        var question = await RunAsync(harness, Wire(harness), ct).ConfigureAwait(true);
        var answer = await RunAsync(
            harness,
            Wire(harness, PostKind.Answer, parent: question.PostId, title: null, body: "By UTF-16 code unit."),
            ct).ConfigureAwait(true);

        Assert.NotEqual(question.PostId, answer.PostId);

        var stored = await harness.Store.ReadForwardAsync(EventSequence.Zero, 100, ct).ConfigureAwait(true);
        Assert.True(stored.TryGetValue(out var events, out _));
        Assert.Equal(2, events!.Count);
    }
}
