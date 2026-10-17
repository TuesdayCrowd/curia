using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// An agent that signs real questions, and the key set that verifies them with whatever status and indices a fact needs
/// (R6.57): the screen's header facts need posts whose <see cref="SignatureVerdict"/> is <see cref="SignatureCheck.Verify"/>'s,
/// served at log indices of the fact's choosing.
/// </summary>
internal sealed class ScreenSigner : IDisposable
{
    internal const string AgentId = "https://agents.example/alice";
    internal const string Kid = "alice-1";

    private readonly string _root = Directory.CreateTempSubdirectory("curia-screen-").FullName;
    private readonly EnrolledAgent _agent;

    internal ScreenSigner()
    {
        Assert.True(new ProfileStore(_root)
            .Create("alice", AgentId, Kid, new Uri("http://forum.test"))
            .TryGetValue(out var agent, out _));
        _agent = agent!;
    }

    /// <summary>A signed question titled <paramref name="title"/>, served honestly (no marking) at <paramref name="logIndex"/>.</summary>
    internal ProvenancePost Question(string title, long? logIndex = null, int index = 0)
    {
        var draft = new PostDraft { Kind = PostKind.Question, Board = "b", Title = title, Body = "A body that is not the title.\nIts second line." };
        Assert.True(SubmissionBuilder.Build(_agent, draft, DateTimeOffset.UnixEpoch).TryGetValue(out var signed, out var error), error?.Type);

        var canonical = Encoding.UTF8.GetString(signed!.Canonical.Span);
        return SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None) with
        {
            PostId = string.Create(CultureInfo.InvariantCulture, $"01SCREENPOST0000000000{index:D4}"),
            Signature = signed.Signature,
            Digest = signed.PrefixedDigest,
            LogIndex = logIndex,
        };
    }

    /// <summary>The key set, with the status words a fact names; none for the default, which is a key set that states none.</summary>
    internal ImmutableArray<ForumJwk> Jwks(
        string? status = null, long? retiredAt = null, long? compromisedAt = null, long? disputedFrom = null)
    {
        using var key = _agent.ExportPublicKey();
        var p = key.ExportParameters(includePrivateParameters: false);
        return
        [
            new ForumJwk(
                "EC", "P-256", "ES256", Kid, Base64Url.EncodeToString(p.Q.X!), Base64Url.EncodeToString(p.Q.Y!), null, null,
                Status: status, RetiredLogIndex: retiredAt, CompromisedLogIndex: compromisedAt, DisputedFrom: disputedFrom),
        ];
    }

    internal static Passage Passage(ProvenancePost post, ImmutableArray<ForumJwk> jwks) => new(post, SignatureCheck.Verify(post, jwks));

    public void Dispose()
    {
        _agent.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
