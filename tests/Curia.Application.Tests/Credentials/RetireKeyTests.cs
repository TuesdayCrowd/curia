using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Domain;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R4.39 at the retirement use case (errata G21, spec section 4.7): a key ends itself, never a sibling, never
/// the last one, and the act is not retroactive.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RetireKeyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Describe<T>(Result<T> result) => result.Match(_ => "ok", e => $"{e.Type} {e.Detail}");

    private static async Task<(KeyActHarness Harness, TestIdentity First, TestIdentity Second)> TwoKeysAsync()
    {
        var harness = new KeyActHarness();
        var (first, second) = (TestIdentity.New("alice-1"), TestIdentity.New("alice-2"));
        await harness.EnrollAsync(Alice, first).ConfigureAwait(false);
        Require(await harness.RotateAsync(Alice, first, second).ConfigureAwait(false));
        return (harness, first, second);
    }

    [Fact]
    public async Task R4_39_ARetirementSignedByASiblingKeyIsRefused()
    {
        var (harness, first, second) = await TwoKeysAsync();
        var stream = await harness.StreamAsync(Alice);

        // Under the sibling's own header, then under the victim's header with the sibling's key.
        var underSibling = await harness.RetireAsync(Alice, first, second);
        var misnamed = await harness.RetireAsync(Alice, first, new TestIdentity(second.Material, new Curia.Canon.Jws.SigningKey("ES256", first.Kid, second.Signing.Private)));

        Assert.Equal(
            $"curia/keys/signature-invalid field=authorization; curia/keys/signature-invalid field=authorization; {stream.Count}",
            $"{Describe(underSibling)}; {Describe(misnamed)}; {(await harness.StreamAsync(Alice)).Count}");
    }

    [Fact]
    public async Task R4_39_TheLastLiveKeyCannotBeRetired()
    {
        var harness = new KeyActHarness();
        var first = TestIdentity.New("alice-1");
        await harness.EnrollAsync(Alice, first);

        var answer = await harness.RetireAsync(Alice, first);

        Assert.Equal($"curia/keys/last-live-key ; 2", $"{Describe(answer)}; {(await harness.StreamAsync(Alice)).Count}");
    }

    [Fact]
    public async Task R4_39_RetirementIsNotRetroactive()
    {
        var (harness, first, _) = await TwoKeysAsync();
        Require(await harness.RetireAsync(Alice, first));
        var stream = await harness.StreamAsync(Alice);

        // A post accepted before the retirement is decided at its old position: re-fold the stream up to it.
        var before = Require(KeyLifecycle.Of([.. stream.Take(stream.Count - 1)], Typed.Agent(Alice))).StatusNow("alice-1");
        var after = Require(KeyLifecycle.Of(stream, Typed.Agent(Alice))).StatusNow("alice-1");
        var resolved = (await harness.KeysOver().ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), ServerTimestamp.At(harness.Clock.GetUtcNow()), Ct)).Match(_ => "live", e => e.Type);

        Assert.Equal("Active; Retired; curia/keys/retired", $"{before}; {after}; {resolved}");
    }

    [Fact]
    public async Task R4_39_ARetirementSignedUnderARotationTypIsRefused()
    {
        var (harness, first, _) = await TwoKeysAsync();
        var text = RetirementStatement(Alice, first.Kid, await harness.LatestIndexAsync(Alice));

        var answer = await harness.Retirer().RetireAsync(
            Typed.Agent(Alice),
            Typed.Kid(first.Kid),
            Require(StatementText.Parse(text)),
            harness.Sign(text, KeyStatements.RotationHeaderTyp, first.Signing),
            Ct);

        Assert.Equal("curia/keys/signature-invalid field=authorization", Describe(answer));
    }

    [Fact]
    public async Task R4_39_ARetirementOfAKeyAlreadyRetiredOrUnknownIsRefused()
    {
        var (harness, first, _) = await TwoKeysAsync();
        Require(await harness.RetireAsync(Alice, first));

        var again = await harness.RetireAsync(Alice, first);
        var unknown = await harness.RetireAsync(Alice, TestIdentity.New("nobody"));

        Assert.Equal("curia/keys/not-live ; curia/keys/unknown-key ", $"{Describe(again)}; {Describe(unknown)}");
    }

    [Fact]
    public async Task R4_39_ARetirementWhosePrevIsNotTheLatestIsRefusedNamingTheLatest()
    {
        var (harness, first, _) = await TwoKeysAsync();

        var answer = await harness.RetireAsync(Alice, first, prev: 1);

        Assert.Equal("curia/keys/lifecycle-moved 2", Describe(answer));
    }

    [Fact]
    public async Task R4_39_ARetirementRecordsItsEntryAndIsFoldedByTheLifecycle()
    {
        var (harness, first, _) = await TwoKeysAsync();

        var recorded = Require(await harness.RetireAsync(Alice, first));
        var lifecycle = Require(KeyLifecycle.Of(await harness.StreamAsync(Alice), Typed.Agent(Alice)));

        Assert.Equal(
            "alice-1 at 3 by none; live: 1",
            $"{recorded.Kid} at {recorded.LogIndex} by {recorded.AuthorizedBy ?? "none"}; live: {lifecycle.LiveCount()}");
    }
}
