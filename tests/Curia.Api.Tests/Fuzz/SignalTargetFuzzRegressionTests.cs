using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R11.6 (add.): the closed fuzzer's finding on a tree whose <c>db/0007</c> indexed the canonical text, promoted to named tests. The random pass (seed <c>0000001diyh1</c>, draw 619) put a NUL escape in a
/// verification's <c>nonce</c>, re-signed it, and the Forum answered 503 <c>curia/store/unavailable</c> to a post that ADMIT admits (D61 item 12) and that the signature covers; the closed pass's
/// <c>nul</c> variation did the same to the string members of a vote's and a verification's envelope. A valid post is a 201, and any member at all is never a 5xx.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("fuzz")]
public sealed class SignalTargetFuzzRegressionTests(FuzzForumFixture forum) : IClassFixture<FuzzForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int Status, string Body)> SendNulAsync(FuzzContext context, ExemplarRow row, string address)
    {
        context.Prepared = null;
        if (row.Prepare is not null)
        {
            forum.Clock.Advance(row.PrepareStep);
            await row.Prepare(context, Ct);
        }

        if (row.CreatesPosts) forum.Clock.Advance(TimeSpan.FromHours(1));

        var model = await row.Build(context, Ct);
        var part = model.Parts().Single(p => p.Address == address);
        var variation = Variations.Closed.Single(v => v.Id == "nul");
        using var request = model.Render(part, variation.Make(part, model.ValueOf(part)), CopyKind.ReSigned);
        using var response = await context.SendOneAsync(request, Ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>FZ1 (random draw 619): a vote and a verification whose nonce is a NUL escape, re-signed, are accepted.</summary>
    [Theory]
    [InlineData("vote")]
    [InlineData("verification")]
    public async Task R11_6_FZ1_ASignedNulEscapeInTheNonceIsAcceptedAndNotAStoreFault(string variant)
    {
        using var context = await FuzzContext.SeedAsync(forum, Ct);
        var row = Exemplars.All.Single(r => r.Route == "POST /v1/posts" && r.Variant == variant);

        var (status, body) = await SendNulAsync(context, row, "json:/envelope/nonce");

        Assert.True(status == 201, $"{variant} with a NUL escape in its nonce answered {status.ToString(CultureInfo.InvariantCulture)}: {body}");
    }

    /// <summary>FZ2: every string member of a vote's and a verification's envelope, given a NUL escape and re-signed, is answered by the Forum's rules (accepted or refused as a request) and never a 5xx.</summary>
    [Theory]
    [InlineData("vote")]
    [InlineData("verification")]
    public async Task R11_6_FZ2_NoStringMemberOfASignedSignalWithANulEscapeIsAServerFault(string variant)
    {
        using var context = await FuzzContext.SeedAsync(forum, Ct);
        var row = Exemplars.All.Single(r => r.Route == "POST /v1/posts" && r.Variant == variant);
        context.Prepared = null;
        var addresses = (await row.Build(context, Ct)).Parts()
            .Where(p => p.Address.StartsWith("json:/envelope/", StringComparison.Ordinal) && Variations.Closed.Single(v => v.Id == "nul").Applies(p))
            .Select(p => p.Address)
            .ToList();
        Assert.True(addresses.Count >= 5, "the row has too few string members for the probe to mean anything");

        var faults = new List<string>();
        foreach (var address in addresses)
        {
            try
            {
                var (status, body) = await SendNulAsync(context, row, address);
                if (status >= 500) faults.Add($"{address}: {status.ToString(CultureInfo.InvariantCulture)} {body}");
            }
            catch (Exception e) when (e is NotReSignableException)
            {
                // An envelope the variation leaves without a canonical form has no re-signed copy; the closed pass sends it unsigned and the oracle there is the same.
            }
        }

        Assert.True(faults.Count == 0, string.Join('\n', faults));
    }
}
