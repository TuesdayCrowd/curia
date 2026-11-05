#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Globalization;
using Curia.Application.Credentials;
using Curia.Application.Ingest;
using Curia.Application.Tests.Credentials;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// A <see cref="VerifiedSubmission"/> of a chosen kind, made the way the route makes one (ADMIT, then VERIFY), for the facts of the pure layer that need a submission and nothing else (errata G57,
/// LCB28). The agent is <see cref="KeyActHarness.Alice"/>, enrolled with one key; the content is the minimum Table 9 asks of the kind.
/// </summary>
internal static class SubmissionOf
{
    internal static async Task<VerifiedSubmission> KindAsync(PostKind kind, string author)
    {
        _ = author;
        var identity = TestIdentity.New("alice-1");
        var harness = new KeyActHarness();
        await harness.EnrollAsync(KeyActHarness.Alice, identity).ConfigureAwait(true);
        var pipeline = new IngestPipeline(harness.KeysOver(), harness.Events, harness.Verifiers, harness.Clock);

        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String(PostKinds.Wire(kind))),
            new("author", new JsonValue.String(KeyActHarness.Alice)),
            new("board", new JsonValue.String("canonicalization")),
        };
        if (kind == PostKind.Question) members.Add(new("title", new JsonValue.String("Member ordering in JCS")));
        if (kind is PostKind.Question or PostKind.Revision or PostKind.Answer or PostKind.Comment) members.Add(new("body", new JsonValue.String("How does JCS order object members?")));
        if (kind is PostKind.Answer or PostKind.Comment) members.Add(new("parent", new JsonValue.String("01HZZZZZZZZZZZZZZZZZZZZZZZ")));
        if (kind == PostKind.Revision)
        {
            members.Add(new("parent", new JsonValue.String("01HZZZZZZZZZZZZZZZZZZZZZZZ")));
            members.Add(new("prev", new JsonValue.String("sha256:" + new string('a', 64))));
        }

        if (kind == PostKind.Vote)
        {
            members.Add(new("target", new JsonValue.String("sha256:" + new string('b', 64))));
            members.Add(new("endorse", new JsonValue.Bool(true)));
            members.Add(new("predicted_endorsement_bp", new JsonValue.Number(5000)));
            members.Add(new("epoch", new JsonValue.Number(1)));
        }

        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(KeyActHarness.Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("0123456789abcdef0123456789abcdef")));

        var envelope = new JsonValue.Object([.. members]);
        var canonical = KeyActHarness.Require(CanonicalJson.CanonicalizeWithNfc(envelope));
        var crypto = new TestEs256();
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { [TestEs256.Alg] = crypto },
            new Dictionary<string, IContentVerifier> { [TestEs256.Alg] = crypto });
        var signature = KeyActHarness.Require(jws.Sign(canonical, identity.Signing));
        var wire = KeyActHarness.Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(
        [
            new("envelope", envelope),
            new("signature", new JsonValue.String(signature.Compact)),
        ]))).ToArray();

        var admitted = KeyActHarness.Require(pipeline.Admit(wire));
        return KeyActHarness.Require(await pipeline.VerifyAsync(admitted, Typed.Agent(KeyActHarness.Alice), CancellationToken.None).ConfigureAwait(true));
    }
}
