using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// GF9 (R4.38 (add.), spec sections 4.9, 4.12 and 4.13): a Forum that enables neither kind refuses the verbs, and the
/// refusal changes nothing of the profile. The Forum here is <c>StubLog</c> in its guarded mode, which serves the
/// refusal documents <c>LifecycleRefusals</c> holds; that those are what the guarded Forum really serves is held in
/// <c>Curia.Api.Tests</c>, on <c>GuardedForumFixture</c>.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class GuardedLifecycleVerbTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int LifecycleRequests(StubLog log) =>
        log.Requests.Count(r => r.StartsWith("POST /v1/agents/", StringComparison.Ordinal)
            && (r.EndsWith("/keys", StringComparison.Ordinal) || r.EndsWith("/retire", StringComparison.Ordinal)));

    /// <summary>The sha256 of <c>identity.json</c>, <c>signing-key.pem</c>, and the sorted <c>keys/</c> listing.</summary>
    private static string Fingerprint(StubLog log)
    {
        var directory = log.Store.DirectoryFor("alice");
        var parts = new List<string>();
        foreach (var name in new[] { "identity.json", "signing-key.pem" })
        {
            var path = Path.Combine(directory, name);
            parts.Add(name + "=" + (File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "absent"));
        }

        var keys = Path.Combine(directory, "keys");
        parts.Add("keys=" + (Directory.Exists(keys) ? string.Join(",", Directory.EnumerateFileSystemEntries(keys).Select(Path.GetFileName).Order(StringComparer.Ordinal)) : "absent"));
        return string.Join("|", parts);
    }

    private static (EnrolledAgent Agent, ForumSession Session) Loaded(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Title);
        return (agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System));
    }

    [Fact]
    public async Task R4_38_RotateAgainstAGuardedForumLeavesTheProfileByteIdentical()
    {
        using var log = new StubLog { GuardsLifecycle = true };
        var (agent, session) = Loaded(log);
        using (agent)
        {
            var before = Fingerprint(log);

            var (exit, _, stderr) = await KeyActRig.CaptureAsync(
                () => Program.RotateKeyAsync(log.Client(), session, agent, log.Store, null, Ct));

            Assert.Equal(ExitCode.Denied, exit);
            Assert.Contains("refused   this Forum does not enable key rotation (curia/keys/lifecycle-not-enabled); nothing was changed", stderr, StringComparison.Ordinal);
            Assert.Equal(1, LifecycleRequests(log));
            Assert.Equal(before, Fingerprint(log));
        }
    }

    [Fact]
    public async Task R4_38_RetireAgainstAGuardedForumLeavesTheProfileByteIdentical()
    {
        using var log = new StubLog();

        // A retained key to retire: a rotation, made while the stub accepts one.
        var (first, firstSession) = Loaded(log);
        using (first)
        {
            var (rotated, _, rotateError) = await KeyActRig.CaptureAsync(
                () => Program.RotateKeyAsync(log.Client(), firstSession, first, log.Store, null, Ct));
            Assert.True(rotated == 0, rotateError);
        }

        log.GuardsLifecycle = true;
        log.Requests.Clear();
        var (agent, session) = Loaded(log);
        using (agent)
        {
            var before = Fingerprint(log);

            var (exit, _, stderr) = await KeyActRig.CaptureAsync(
                () => Program.RetireKeyAsync(log.Client(), session, log.Store, agent.Profile, StubLog.AgentKid, Ct));

            Assert.Equal(ExitCode.Denied, exit);
            Assert.Contains("refused   this Forum does not enable key retirement (curia/keys/lifecycle-not-enabled); nothing was changed", stderr, StringComparison.Ordinal);
            Assert.Equal(1, LifecycleRequests(log));
            Assert.Equal(before, Fingerprint(log));
        }
    }

    /// <summary>Positive control: the same stub with the guard off accepts, and the hash comparison sees the change it makes.</summary>
    [Fact]
    public async Task R4_38_TheSameStubUnguardedChangesTheProfile()
    {
        using var log = new StubLog();
        var (agent, session) = Loaded(log);
        using (agent)
        {
            var before = Fingerprint(log);

            var (exit, _, stderr) = await KeyActRig.CaptureAsync(
                () => Program.RotateKeyAsync(log.Client(), session, agent, log.Store, null, Ct));

            Assert.True(exit == 0, stderr);

            // The stub asks for a nonce first, as the Forum does, so the act is sent twice; the guarded stub refuses before that.
            Assert.True(LifecycleRequests(log) >= 1);
            Assert.NotEqual(before, Fingerprint(log));
        }
    }

    [Fact]
    public void R4_38_TheStandInServesTheDocumentsAGuardedForumHasForEachKind()
    {
        foreach (var (document, detail) in new[]
        {
            (LifecycleRefusals.Rotation, "key rotation is not enabled on this Forum"),
            (LifecycleRefusals.Retirement, "key retirement is not enabled on this Forum"),
        })
        {
            var root = JsonDocument.Parse(document).RootElement;
            Assert.Equal(LifecycleRefusals.Type, root.GetProperty("type").GetString());
            Assert.Equal(detail, root.GetProperty("detail").GetString());
            Assert.Equal(3, root.EnumerateObject().Count());
        }

        Assert.Equal((int)HttpStatusCode.Forbidden, LifecycleRefusals.Status);
    }
}
