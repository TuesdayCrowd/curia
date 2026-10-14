using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The reference client's key verbs against the Forum itself (errata G21, spec sections 4.7, 4.9 and 4.12): the library
/// methods <c>curia rotate</c> and <c>curia retire</c> are built from, driven end to end over HTTP, so that the paths
/// and the wire shape they use are held to the real routes and not to a stand-in this repository wrote. On a real
/// socket, because the aliasing of <c>%2F</c> and <c>%252F</c> is Kestrel's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ClientKeyActTests(KeyRouteKestrelFixture forum) : IClassFixture<KeyRouteKestrelFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-client-keyact-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProfileStore Store => new(_home);

    private ForumClient Client => new(forum.Client, forum.Client.BaseAddress!);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<EnrolledAgent> EnrolAsync(string agentId)
    {
        var slug = "k-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(Store.Create(slug, agentId, slug + "-0", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error), error?.Title);
        var enrolled = await Client.EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

    private ForumSession SessionFor(EnrolledAgent agent) => new(Client, agent, Store, forum.Clock);

    private async Task<KeySetDocument> KeySetOfAsync(string agentId)
    {
        var read = await Client.GetKeySetAsync(agentId, Ct);
        Assert.True(read.TryGetValue(out var keySet, out var refusal), refusal?.Summary);
        return keySet!;
    }

    /// <summary>
    /// Rotate the identity onto a fresh key and keep it, as <c>curia rotate</c> does; returns the profile as written
    /// and the receipt the Forum answered.
    /// </summary>
    private async Task<(AgentProfile Profile, KeyActReceipt Receipt)> RotateAsync(EnrolledAgent agent, bool keep)
    {
        var keySet = await KeySetOfAsync(agent.Profile.AgentId);
        var prev = keySet.Lifecycle!.Value.Max();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = new InProcessSigner(key, agent.Profile.Slug + "-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.True(Curia.Client.KeyActs.BuildRotation(agent, next, prev).TryGetValue(out var request, out var buildError), buildError?.Title);

        var rotated = await SessionFor(agent).RotateKeyAsync(agent.Profile.AgentId, request!, Ct);
        Assert.True(rotated.TryGetValue(out var receipt, out var refusal), refusal?.Summary + " " + refusal?.Error.Type);
        Assert.Equal(next.Kid, receipt!.Kid);
        Assert.Equal(agent.Profile.Kid, receipt.AuthorizedBy);

        var profile = keep ? Store.CompleteRotation(agent.Profile, next, key, receipt) : agent.Profile;
        return (profile, receipt);
    }

    /// <summary>
    /// A rotation and then the retirement of the key it rotated from, over the Forum's routes, for an identity whose
    /// id holds nothing special and for ones that hold a literal percent escape, a space and a lone percent sign.
    /// The key set the Forum then publishes is what this profile recorded, to the entry: no FOREIGN and no MISSING.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("a%2Fb")]
    [InlineData("a%5Cb")]
    [InlineData("a%20b")]
    [InlineData("a%b")]
    public async Task R4_18_TheClientRotatesAndThenRetiresOverTheRoutes(string stem)
    {
        var agentId = $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";
        using var agent = await EnrolAsync(agentId);
        var oldKid = agent.Profile.Kid;

        var (rotated, rotation) = await RotateAsync(agent, keep: true);

        // A new run of the client: it loads what the rotation wrote, signs with the new key, and retires the old one.
        Assert.True(Store.Load(agent.Profile.Slug).TryGetValue(out var reloaded, out var loadError), loadError?.Title);
        using (reloaded)
        {
            Assert.Equal(rotation.Kid, reloaded!.Profile.Kid);

            Assert.True(Store.LoadRetained(reloaded.Profile, oldKid).TryGetValue(out var retiring, out var retiringError), retiringError?.Title);
            using (retiring as IDisposable)
            {
                var prev = (await KeySetOfAsync(agentId)).Lifecycle!.Value.Max();
                Assert.True(Curia.Client.KeyActs.BuildRetirement(retiring!, agentId, prev).TryGetValue(out var request, out var buildError), buildError?.Title);

                var retired = await SessionFor(reloaded).RetireKeyAsync(agentId, oldKid, request!, Ct);
                Assert.True(retired.TryGetValue(out var receipt, out var refusal), refusal?.Summary + " " + refusal?.Error.Type);
                Assert.Equal(oldKid, receipt!.Kid);

                var profile = Store.CompleteRetirement(reloaded.Profile, receipt);
                Assert.True(Assert.Single(profile.Retained).Retired);
                Assert.Equal(2, profile.Acts.Length);

                var keySet = await KeySetOfAsync(agentId);
                var published = keySet.Keys.ToDictionary(k => k.Kid, StringComparer.Ordinal);
                Assert.Equal("retired", published[oldKid].Status);
                Assert.Equal(receipt.LogIndex, published[oldKid].RetiredLogIndex);
                Assert.Equal("active", published[rotation.Kid].Status);
                Assert.Equal(rotation.LogIndex, published[rotation.Kid].LogIndex);

                Assert.Empty(LifecycleWatch.Compare(profile, keySet));
            }
        }

        Assert.Equal(oldKid, Assert.Single(rotated.Retained).Kid);
    }

    /// <summary>A rotation this profile did not record is, to its owner, an entry the profile did not make (R4.39).</summary>
    [Fact]
    public async Task R4_39_ARotationTheProfileDidNotRecordIsForeign()
    {
        var agentId = $"https://agents.example/foreign-{Guid.NewGuid().ToString("N")[..8]}";
        using var agent = await EnrolAsync(agentId);
        Assert.Empty(LifecycleWatch.Compare(agent.Profile, await KeySetOfAsync(agentId)));

        var (_, receipt) = await RotateAsync(agent, keep: false);

        var findings = LifecycleWatch.Compare(agent.Profile, await KeySetOfAsync(agentId));
        Assert.Equal([new LifecycleFinding(true, "rotation", receipt.LogIndex)], findings);
    }

    /// <summary>
    /// <c>a/b</c> and <c>a%2Fb</c> are two identities that a host's path cannot tell apart, and the client names each
    /// by its own escaped segment. A token for one sent to the other's route is refused as not its own, through the
    /// library methods and not by hand.
    /// </summary>
    [Fact]
    public async Task R4_18_TheEscapedAndTheDoubleEscapedSlashNameDifferentIdentitiesThroughTheClient()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var a = await EnrolAsync($"https://agents.example/a/b-{suffix}");
        using var b = await EnrolAsync($"https://agents.example/a%2Fb-{suffix}");

        var prev = (await KeySetOfAsync(a.Profile.AgentId)).Lifecycle!.Value.Max();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = new InProcessSigner(key, "next-" + suffix);
        Assert.True(Curia.Client.KeyActs.BuildRotation(a, next, prev).TryGetValue(out var request, out _));

        // A's session, A's rotation, B's route.
        var crossed = await SessionFor(a).RotateKeyAsync(b.Profile.AgentId, request!, Ct);

        Assert.False(crossed.TryGetValue(out _, out var refusal));
        Assert.Equal(403, refusal!.Status);
        Assert.Equal("curia/keys/not-own", refusal.Error.Type);
        Assert.Single((await KeySetOfAsync(b.Profile.AgentId)).Keys);

        // And through its own route it rotates.
        var own = await SessionFor(a).RotateKeyAsync(a.Profile.AgentId, request!, Ct);
        Assert.True(own.TryGetValue(out _, out var ownRefusal), ownRefusal?.Summary);
    }
}

/// <summary>
/// GF9's stand-in held to the Forum it stands in for (R4.38 (add.), spec section 4.7): what <c>StubLog</c>'s guarded mode
/// serves a lifecycle route is <c>LifecycleRefusals</c>, and here the guarded Forum is asked and its answer compared
/// with it, member for member.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class GuardedClientRefusalTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-client-guarded-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// For each lifecycle route the document the guarded Forum serves equals the stand-in's, status and body, and a
    /// member present on one side only fails: both are rendered canonically and compared whole. The served body
    /// carries no per-request member, so none is excluded.
    /// </summary>
    [Fact]
    public async Task R4_38_TheGuardedRefusalIsTheDocumentTheClientStandInServes()
    {
        var store = new ProfileStore(_home);
        Assert.True(store.Create("g", "https://agents.example/guarded-client-" + Guid.NewGuid().ToString("N")[..8], "g-0", forum.Client.BaseAddress!).TryGetValue(out var agent, out _));
        using (agent)
        {
            var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
            Assert.True((await client.EnrolAsync(agent!, Ct)).TryGetValue(out _, out var enrolRefusal), enrolRefusal?.Summary);
            var session = new ForumSession(client, agent!, store, forum.Clock);
            var agentId = agent!.Profile.AgentId;

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var next = new InProcessSigner(key, "g-next");
            Assert.True(Curia.Client.KeyActs.BuildRotation(agent, next, 0).TryGetValue(out var rotation, out _));
            Assert.True(Curia.Client.KeyActs.BuildRetirement(agent.Signer, agentId, 0).TryGetValue(out var retirement, out _));

            var served = new[]
            {
                (Route: "rotation", Result: await session.RotateKeyAsync(agentId, rotation!, Ct), Stand: LifecycleRefusals.Rotation),
                (Route: "retirement", Result: await session.RetireKeyAsync(agentId, agent.Profile.Kid, retirement!, Ct), Stand: LifecycleRefusals.Retirement),
            };

            foreach (var (route, result, stand) in served)
            {
                Assert.False(result.TryGetValue(out _, out var refusal), route);
                Assert.Equal(LifecycleRefusals.Status, refusal!.Status);
                Assert.Equal(LifecycleRefusals.Type, refusal.Error.Type);

                Assert.NotNull(refusal.Document);
                Assert.True(JsonReader.Parse(System.Text.Encoding.UTF8.GetBytes(stand), AdmitLimits.Default).TryGetValue(out var expected, out _));
                Assert.True(CanonicalJson.Canonicalize(refusal.Document).TryGetValue(out var actualBytes, out _));
                Assert.True(CanonicalJson.Canonicalize(expected!).TryGetValue(out var expectedBytes, out _));
                Assert.Equal(System.Text.Encoding.UTF8.GetString(expectedBytes.Span), System.Text.Encoding.UTF8.GetString(actualBytes.Span));
            }
        }

        Assert.Equal((int)HttpStatusCode.Forbidden, LifecycleRefusals.Status);
    }
}
