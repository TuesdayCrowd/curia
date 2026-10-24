using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// D67: a write whose post id is in the path is proved over the URL the host rebuilds from its decoded
/// path, not over the display form of the URL that was sent. The expected <c>htu</c> of every row is
/// written by hand from the host's rule (every escape decoded once except <c>%2F</c> and <c>%5C</c>, then
/// the framework's re-escape), because a table computed by <c>KeyActUrls.ProofTarget</c> would agree with
/// whatever that function does and prove nothing about the two routes that call it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PathIdWriteProofTests : IDisposable
{
    private readonly StubLog _log = new();

    public static TheoryData<string, string> Ids => new()
    {
        { "a b", "a%20b" },
        { "sha256:abc", "sha256:abc" },
        { "100%", "100%25" },
        { "\u00E9", "%C3%A9" },
        { "a%2Fb", "a%2Fb" },
    };

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private ForumSession Session(EnrolledAgent agent) => new(_log.Client(), agent, _log.Store, TimeProvider.System);

    private EnrolledAgent Alice() =>
        _log.Store.Load("alice").TryGetValue(out var agent, out var error)
            ? agent!
            : throw new InvalidOperationException(error!.Type);

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Flag_ProvesTheUrlTheHostRebuilds(string id, string hostSegment)
    {
        using var agent = Alice();
        await Session(agent).FlagAsync(id, "spam", "a reason", TestContext.Current.CancellationToken);

        Assert.NotEmpty(_log.WriteProofs);
        Assert.All(
            _log.WriteProofs,
            p => Assert.Equal($"http://forum.test/v1/posts/{hostSegment}/flags", p.Htu));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Accept_ProvesTheUrlTheHostRebuilds(string id, string hostSegment)
    {
        using var agent = Alice();
        await Session(agent).AcceptAsync(id, TestContext.Current.CancellationToken);

        Assert.NotEmpty(_log.WriteProofs);
        Assert.All(
            _log.WriteProofs,
            p => Assert.Equal($"http://forum.test/v1/posts/{hostSegment}/accept", p.Htu));
    }

    /// <summary>
    /// The proof changes and the wire does not. The rows are the ones whose sent form differs from the raw id
    /// as the request line records it (a literal <c>%2F</c> goes as <c>%252F</c>), so an id sent unescaped fails.
    /// </summary>
    [Theory]
    [InlineData("a b", "a%20b")]
    [InlineData("a%2Fb", "a%252Fb")]
    [InlineData("100%", "100%25")]
    public async Task TheSentUrlIsStillEscaped(string id, string sentSegment)
    {
        using var agent = Alice();
        await Session(agent).FlagAsync(id, "spam", "a reason", TestContext.Current.CancellationToken);

        Assert.Contains($"POST /v1/posts/{sentSegment}/flags", _log.Requests);
    }

    [Fact]
    public async Task AFixedPathIsProvedAsBefore()
    {
        using var agent = Alice();
        var posted = await Session(agent).SubmitAsync(
            SubmissionBuilder.Build(
                    agent,
                    new PostDraft { Kind = PostKind.Question, Board = "b", Title = "Fixed?", Body = "Body of: Fixed?" },
                    DateTimeOffset.UnixEpoch)
                .TryGetValue(out var signed, out var error)
                ? signed!.Wire
                : throw new InvalidOperationException(error!.Type),
            MarkingMode.Datamark,
            TestContext.Current.CancellationToken);
        Assert.True(posted.TryGetValue(out _, out var refusal), refusal?.Summary);

        // The marking travels in the query, which the proof's htu does not carry (RFC 9449 section 4.2).
        Assert.Contains("?marking=datamark", _log.Queries);
        Assert.All(_log.WriteProofs, p => Assert.Equal("http://forum.test/v1/posts", p.Htu));
    }
}
