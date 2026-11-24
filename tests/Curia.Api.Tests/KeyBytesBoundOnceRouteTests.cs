using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G78 (R4.45), KBO14: over HTTP, against the real route and the real client library, a rotation that names the public key of a key the identity retired is answered <c>409</c> <c>curia/keys/key-already-bound</c>,
/// appends nothing and leaves the key set as it was. (<c>R4_18_EveryRefusalTheUseCasesReturnIsMappedByTheRoutes</c> holds that the type has its row in <c>StatusForKeyAct</c>: it goes red by itself when the type
/// exists and the row does not.) Written against the tree before the rule exists: the rotation is honoured and the answer is a receipt.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeyBytesBoundOnceRouteTests(KeyRouteKestrelFixture forum) : IClassFixture<KeyRouteKestrelFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-keybytes-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProfileStore Store => new(_home);

    private ForumClient Client => new(forum.Client, forum.Client.BaseAddress!);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ForumSession SessionFor(EnrolledAgent agent) => new(Client, agent, Store, forum.Clock);

    private async Task<KeySetDocument> KeySetOfAsync(string agentId)
    {
        var read = await Client.GetKeySetAsync(agentId, Ct);
        Assert.True(read.TryGetValue(out var keySet, out var refusal), refusal?.Summary);
        return keySet!;
    }

    private static string Listing(KeySetDocument keySet) =>
        string.Join("; ", keySet.Keys.Select(k => $"{k.Kid} {k.Status} at {k.LogIndex}")) + " | lifecycle " + string.Join(",", keySet.Lifecycle!.Value);

    [Fact]
    public async Task KBO14_TheRouteRefusesARotationNamingARetiredKeysBytesAndAppendsNothing()
    {
        var agentId = $"https://agents.example/kbo-{Guid.NewGuid().ToString("N")[..8]}";
        var slug = "kbo-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(Store.Create(slug, agentId, slug + "-0", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error), error?.Title);
        using var enrolled = agent;
        Assert.True((await Client.EnrolAsync(agent!, Ct)).TryGetValue(out _, out var enrolRefusal), enrolRefusal?.Summary);
        var oldKid = agent!.Profile.Kid;

        // Rotate to a second key and keep it, then retire the first: the scene the harm needs, built as `curia rotate` and `curia retire` build it.
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = new InProcessSigner(second, slug + "-1");
        Assert.True(Curia.Client.KeyActs.BuildRotation(agent, next, (await KeySetOfAsync(agentId)).Lifecycle!.Value.Max()).TryGetValue(out var request, out var buildError), buildError?.Title);
        Assert.True((await SessionFor(agent).RotateKeyAsync(agentId, request!, Ct)).TryGetValue(out var receipt, out var rotateRefusal), rotateRefusal?.Summary);
        Store.CompleteRotation(agent.Profile, next, second, receipt!);

        Assert.True(Store.Load(slug).TryGetValue(out var reloaded, out var loadError), loadError?.Title);
        using (reloaded)
        {
            Assert.True(Store.LoadRetained(reloaded!.Profile, oldKid).TryGetValue(out var retiring, out var retainedError), retainedError?.Title);
            using (retiring as IDisposable)
            {
                Assert.True(Curia.Client.KeyActs.BuildRetirement(retiring!, agentId, (await KeySetOfAsync(agentId)).Lifecycle!.Value.Max()).TryGetValue(out var retirement, out var retireBuildError), retireBuildError?.Title);
                Assert.True((await SessionFor(reloaded).RetireKeyAsync(agentId, oldKid, retirement!, Ct)).TryGetValue(out _, out var retireRefusal), retireRefusal?.Summary);
            }

            var keySetBefore = Listing(await KeySetOfAsync(agentId));
            var boundBefore = await forum.CountEventsAsync("agent.key-bound", agentId, Ct);

            // The harm: the live key authorizes a rotation to the retired key's own bytes, under a new kid.
            using var retainedKey = ECDsa.Create();
            retainedKey.ImportFromPem(await File.ReadAllTextAsync(Store.RetainedKeyFile(slug, oldKid), Ct));
            using var reborn = new InProcessSigner(retainedKey, slug + "-reborn");
            Assert.True(Curia.Client.KeyActs.BuildRotation(reloaded, reborn, (await KeySetOfAsync(agentId)).Lifecycle!.Value.Max()).TryGetValue(out var harm, out var harmBuildError), harmBuildError?.Title);

            var answer = await SessionFor(reloaded).RotateKeyAsync(agentId, harm!, Ct);

            var said = answer.TryGetValue(out var honoured, out var refusal) ? $"honoured at {honoured!.LogIndex}" : $"{refusal!.Status} {refusal.Error.Type}";
            Assert.Equal(
                $"409 curia/keys/key-already-bound; agent.key-bound events {boundBefore}; key set {keySetBefore}",
                $"{said}; agent.key-bound events {await forum.CountEventsAsync("agent.key-bound", agentId, Ct)}; key set {Listing(await KeySetOfAsync(agentId))}");
        }
    }
}
