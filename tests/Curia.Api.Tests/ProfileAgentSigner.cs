using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Client;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// A signed submission by a client profile's agent that does not go through <see cref="SubmissionBuilder.Build"/>. Build asks the Forum's
/// admission function (R8.64, R8.73 and, since errata G56, R8.85), so a post a fixture must <b>store</b> on a board the door now refuses (R8.85 is
/// applied to no stored post) has to be composed here and handed to <see cref="ForumFixture.AppendStoredPostAsync"/>, never to the door. Members are
/// the Table 9 set Build writes; the signature is the profile's own (<c>agent.Signer</c>), so the stored post verifies under the key the Forum serves.
/// </summary>
internal static class ProfileAgentSigner
{
    internal static byte[] Sign(EnrolledAgent agent, PostKind kind, string board, string body, string? title, string? parent, string? prev, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(agent.Profile.AgentId)));
        members.Add(new("board", new JsonValue.String(board)));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (prev is not null) members.Add(new("prev", new JsonValue.String(prev)));
        if (title is not null) members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String(body)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(createdAt.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))));
        var envelope = new JsonValue.Object(members.ToImmutable());

        Assert.True(CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out _));
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() },
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal));
        Assert.True(jws.Sign(canonical, agent.Signer).TryGetValue(out var signature, out var error), error?.Type);

        var submission = new JsonValue.Object(
        [
            new("envelope", envelope),
            new("signature", new JsonValue.String(signature!.Compact)),
        ]);
        Assert.True(CanonicalJson.CanonicalizeWithNfc(submission).TryGetValue(out var wire, out _));
        return wire.ToArray();
    }
}
