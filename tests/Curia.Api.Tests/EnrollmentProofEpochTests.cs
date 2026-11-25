using System.Diagnostics.CodeAnalysis;
using System.Net;
using Xunit;
using static Curia.Api.Tests.EnrollProofProbe;

namespace Curia.Api.Tests;

/// <summary>
/// EPP11 (R4.47): the epoch, both sides, through the real nonce store and a fake clock. A fixture of its own, because it moves the Forum's clock by minutes. A proof carrying the nonce of epoch
/// <c>e</c> is accepted in <c>e</c>, accepted in <c>e + 1</c> and refused <c>proof-nonce-stale</c> in <c>e + 2</c>; a nonce this Forum did not choose is refused in all three. The store's own rule is
/// <c>PostgresDpopNonceStoreTests</c>'; this is the route's use of it. (The store has no upper bound, Amendment 2 ruling 22; no row here goes above <c>e + 2</c> or below <c>e</c>, which would pin that.)
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class EnrollmentProofEpochTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R4_47_EPP11_ANonceIsAcceptedInItsEpochAndTheNextAndRefusedInTheOneAfter()
    {
        var client = forum.Client;
        var toBoundary = 300 - (forum.Now.ToUnixTimeSeconds() % 300);
        if (toBoundary != 300) forum.Clock.Advance(TimeSpan.FromSeconds(toBoundary));
        var nonce = await NonceAsync(client, Ct);

        for (var epoch = 0; epoch <= 2; epoch++)
        {
            var bogus = NewAgent($"epp11-bogus-{epoch}");
            var bogusProof = Proof(bogus, Audience(client), "a-nonce-the-forum-never-chose", forum.Now);
            var held = NewAgent($"epp11-held-{epoch}");
            var proof = Proof(held, Audience(client), nonce, forum.Now);

            if (epoch < 2)
            {
                var accepted = await PostAsync(client, held, proof, Ct);
                Assert.True(accepted.Status == HttpStatusCode.Created, $"epoch e+{epoch}: the nonce of epoch e was refused: {accepted.Says}");
            }

            AssertRefused(await PostAsync(client, bogus, bogusProof, Ct), "proof-nonce-stale");
            if (epoch == 2) AssertRefused(await PostAsync(client, held, proof, Ct), "proof-nonce-stale");

            forum.Clock.Advance(TimeSpan.FromMinutes(5));
        }
    }
}
