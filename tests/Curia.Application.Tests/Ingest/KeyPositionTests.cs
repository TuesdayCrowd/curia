using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Credentials;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Tests.Credentials;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// R6.56 at ingest (errata G21): a post is persisted only where its key is live, because the decision
/// (VERIFY reads the author's stream) and the append (PERSIST) are one act against that stream. The
/// retry loop is <see cref="IngestAttempts"/>; each fact runs the real pipeline over a real
/// <see cref="LogBoundKeys"/> and lets an entry join the author's stream between VERIFY and PERSIST.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyPositionTests
{
    private static readonly TestIdentity First = TestIdentity.New("alice-1");

    private static readonly TestIdentity Second = TestIdentity.New("alice-2");

    private static async Task<(KeyActHarness Harness, IngestPipeline Pipeline)> EnrolledWithTwoKeysAsync()
    {
        var harness = new KeyActHarness();
        await harness.EnrollAsync(KeyActHarness.Alice, First).ConfigureAwait(true);
        KeyActHarness.Require(await harness.RotateAsync(KeyActHarness.Alice, First, Second).ConfigureAwait(true));

        var pipeline = new IngestPipeline(harness.KeysOver(), harness.Events, harness.Verifiers, harness.Clock);
        return (harness, pipeline);
    }

    /// <summary>A Table 9 question by Alice, signed with <paramref name="signer"/>, as the wire submission.</summary>
    private static byte[] Wire(TestIdentity signer)
    {
        var crypto = new TestEs256();
        var envelope = new JsonValue.Object(
        [
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String(PostKinds.Wire(PostKind.Question))),
            new("author", new JsonValue.String(KeyActHarness.Alice)),
            new("board", new JsonValue.String("canonicalization")),
            new("title", new JsonValue.String("Member ordering in JCS")),
            new("body", new JsonValue.String("How does JCS order object members?")),
            new("code_blocks", new JsonValue.Array([])),
            new("refs", new JsonValue.Array([])),
            new("tags", new JsonValue.Array([new JsonValue.String("jcs")])),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String(KeyActHarness.Start.ToString("o", CultureInfo.InvariantCulture))),
            new("nonce", new JsonValue.String("0123456789abcdef0123456789abcdef")),
        ]);

        var canonical = KeyActHarness.Require(CanonicalJson.CanonicalizeWithNfc(envelope));
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { [TestEs256.Alg] = crypto },
            new Dictionary<string, IContentVerifier> { [TestEs256.Alg] = crypto });
        var signature = KeyActHarness.Require(jws.Sign(canonical, signer.Signing));

        var submission = new JsonValue.Object(
        [
            new("envelope", envelope),
            new("signature", new JsonValue.String(signature.Compact)),
        ]);

        return KeyActHarness.Require(CanonicalJson.CanonicalizeWithNfc(submission)).ToArray();
    }

    /// <summary>One attempt: VERIFY, then <paramref name="between"/>, then SCREEN and PERSIST.</summary>
    private static async Task<Result<PostAccepted>> AttemptAsync(
        IngestPipeline pipeline, byte[] wire, Func<Task> between, CancellationToken ct)
    {
        var admitted = KeyActHarness.Require(pipeline.Admit(wire));
        var verified = await pipeline.VerifyAsync(admitted, Typed.Agent(KeyActHarness.Alice), ct).ConfigureAwait(true);
        if (!verified.TryGetValue(out var v, out var verifyError)) return Result<PostAccepted>.Fail(verifyError!);

        await between().ConfigureAwait(true);

        var screened = KeyActHarness.Require(await pipeline.ScreenAsync(v!, ct).ConfigureAwait(true));
        return await pipeline.PersistAsync(screened, ct).ConfigureAwait(true);
    }

    private static async Task<IReadOnlyList<AppendedEvent>> PostsAsync(KeyActHarness harness)
    {
        var all = KeyActHarness.Require(await harness.Events.ReadAllAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        return [.. all.Where(e => e.Event.Type.Value == "post.accepted")];
    }

    [Fact]
    public async Task R6_56_APostVerifiedBeforeItsKeysRetirementIsNotAppendedAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, pipeline) = await EnrolledWithTwoKeysAsync().ConfigureAwait(true);
        var wire = Wire(First);

        var attempts = 0;
        var persistErrors = new List<string>();
        var outcome = await IngestAttempts.RunAsync(
            async token =>
            {
                var first = attempts++ == 0;
                var result = await AttemptAsync(
                    pipeline,
                    wire,
                    async () =>
                    {
                        if (first) KeyActHarness.Require(await harness.RetireAsync(KeyActHarness.Alice, First).ConfigureAwait(true));
                    },
                    token).ConfigureAwait(true);
                if (result.TryGetValue(out _, out var error) is false) persistErrors.Add(error!.Type);
                return result;
            },
            ct).ConfigureAwait(true);

        Assert.Equal([DomainErrors.ConditionConflictType, AuthorKeyErrors.RetiredType], persistErrors);
        Assert.Equal(AuthorKeyErrors.RetiredType, KeyActHarness.Refusal(outcome).Type);
        Assert.Empty(await PostsAsync(harness).ConfigureAwait(true));
    }

    [Fact]
    public async Task R6_56_AnUnrelatedEntryInTheAuthorsStreamOnlyRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, pipeline) = await EnrolledWithTwoKeysAsync().ConfigureAwait(true);
        var wire = Wire(First);

        var attempts = 0;
        var outcome = await IngestAttempts.RunAsync(
            token =>
            {
                var first = attempts++ == 0;
                return AttemptAsync(
                    pipeline,
                    wire,
                    async () =>
                    {
                        if (first)
                            KeyActHarness.Require(await new AttestOwner(harness.Events, harness.Clock).RecordAsync(
                                Curia.Domain.Primitives.Boundary.AgentId.Parse(KeyActHarness.Alice),
                                KeyActHarness.Require(OwnerId.Create("owner-1")),
                                verified: true,
                                OwnerVerificationMethod.Manual,
                                "reviewed by an operator",
                                KeyActHarness.Require(ActorId.Create("operator: tester")),
                                token).ConfigureAwait(true));
                    },
                    token);
            },
            ct).ConfigureAwait(true);

        Assert.Equal(2, attempts);
        Assert.True(outcome.IsOk);
        Assert.Single(await PostsAsync(harness).ConfigureAwait(true));
    }

    [Fact]
    public async Task R6_56_ThreeConflictsAnswerContended()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, pipeline) = await EnrolledWithTwoKeysAsync().ConfigureAwait(true);
        var wire = Wire(First);

        // Every attempt finds the stream moved: a different attestation (the owner may re-attest) each time.
        var attempts = 0;
        var outcome = await IngestAttempts.RunAsync(
            token =>
            {
                attempts++;
                return AttemptAsync(
                    pipeline,
                    wire,
                    async () => KeyActHarness.Require(await new AttestOwner(harness.Events, harness.Clock).RecordAsync(
                        Curia.Domain.Primitives.Boundary.AgentId.Parse(KeyActHarness.Alice),
                        KeyActHarness.Require(OwnerId.Create("owner-1")),
                        verified: true,
                        OwnerVerificationMethod.Manual,
                        "reviewed by an operator, again",
                        KeyActHarness.Require(ActorId.Create("operator: tester")),
                        token).ConfigureAwait(true)),
                    token);
            },
            ct).ConfigureAwait(true);

        Assert.Equal(IngestAttempts.Attempts, attempts);
        Assert.Equal(IngestErrors.ContendedType, KeyActHarness.Refusal(outcome).Type);
        Assert.Empty(await PostsAsync(harness).ConfigureAwait(true));
    }
}
