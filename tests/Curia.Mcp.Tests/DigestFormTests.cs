using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.52 (errata G40, closing D61 item 22): <c>curia_verify</c> refuses an <c>expectedDigest</c> that is not <c>sha256:</c> and sixty-four lowercase
/// hexadecimal digits, <c>NOT SENT</c>, naming the argument and the form, comparing nothing and echoing no value. A well-formed digest of another
/// document is still a failed pin (R11.42), and the matching digest still verifies. Every spelling is made here from the stub's real digest, and the
/// stub's own request list is the account of what was sent.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DigestFormTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-digest-form-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string RealHex => _log.Submission.PrefixedDigest["sha256:".Length..];

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
        return await McpProtocolSession.StartAsync(tools, writer.Agent.Profile.AgentId, agent);
    }

    private string Spelling(string name) => name switch
    {
        "upper-case hex" => "sha256:" + RealHex.ToUpperInvariant(),
        "bare hex" => RealHex,
        "63 hex" => "sha256:" + RealHex[1..],
        "65 hex" => "sha256:" + RealHex + "0",
        "upper-case prefix" => "SHA256:" + RealHex,
        "trailing space" => _log.Submission.PrefixedDigest + " ",
        "non-hex" => "sha256:" + new string('g', 64),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a spelling this fact makes"),
    };

    public static TheoryData<string> Spellings() =>
        ["upper-case hex", "bare hex", "63 hex", "65 hex", "upper-case prefix", "trailing space", "non-hex"];

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task R11_52_APinNotInThePublishedFormIsRefusedByNameAndNothingIsCompared(string spelling)
    {
        await using var session = await StartAsync();
        var pin = Spelling(spelling);
        Assert.NotEqual(_log.Submission.PrefixedDigest, pin);

        var result = await session.CallAsync("curia_verify", new() { ["postId"] = StubLog.PostId, ["expectedDigest"] = pin });

        Assert.True(
            result.IsError && result.Text.StartsWith("NOT SENT", StringComparison.Ordinal),
            $"a pin spelled '{spelling}' was not refused NOT SENT:\n{result.Text}");
        Assert.Contains("expectedDigest", result.Text, StringComparison.Ordinal);
        Assert.Contains("sha256:", result.Text, StringComparison.Ordinal);
        Assert.Contains("lowercase", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("different documents", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(RealHex, result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_log.Requests);
    }

    /// <summary>The control: the matching digest in the published form verifies, so every refusal above is the spelling's doing.</summary>
    [Fact]
    public async Task R11_42_TheMatchingDigestInThePublishedFormStillPins()
    {
        await using var session = await StartAsync();

        var result = await session.CallAsync("curia_verify", new() { ["postId"] = StubLog.PostId, ["expectedDigest"] = _log.Submission.PrefixedDigest });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("pinned", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("pinned      FAILED", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.42 unchanged: a digest in the published form that names another document is a failed pin, worded as one. The refusal of a spelling must not
    /// swallow it.
    /// </summary>
    [Fact]
    public async Task R11_42_AWellFormedDigestOfAnotherDocumentIsStillAFailedPin()
    {
        await using var session = await StartAsync();
        var another = "sha256:" + new string('0', 64);
        Assert.NotEqual(_log.Submission.PrefixedDigest, another);

        var result = await session.CallAsync("curia_verify", new() { ["postId"] = StubLog.PostId, ["expectedDigest"] = another });

        Assert.True(result.IsError, result.Text);
        Assert.Contains("pinned", result.Text, StringComparison.Ordinal);
        Assert.Contains("FAILED", result.Text, StringComparison.Ordinal);
        Assert.Contains("different documents", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT SENT", result.Text, StringComparison.Ordinal);
        Assert.NotEmpty(_log.Requests);
    }
}
