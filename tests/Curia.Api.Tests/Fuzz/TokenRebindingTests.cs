using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R14.10: the re-signed copy of a variation of a proof's <c>jwk</c> rebinds the token's
/// <c>cnf.jkt</c> to the key the proof now carries (spec §4.10, Signing). Without it every such copy
/// was refused at the binding check before the key was built, and reverting 777db55's catch went
/// unseen (Task A5, case 3). The expected thumbprint is <see cref="JwsBuilder.Thumbprint"/>, computed
/// from the key's ECDsa parameters, a different artifact from the rendered strings the rebinding reads.
/// The model is built here, not from the Forum's fixture, so these facts send nothing.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TokenRebindingTests
{
    private const string X = "jws:proof:header/jwk/x";

    [Fact]
    public void R14_10_TheReSignedCopyOfAnUnvariedJwkIsBoundToTheProofsKey()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var model = Model(issuer, dpop, issuedFor: JwsBuilder.Thumbprint(issuer));
        var part = model.Parts().Single(p => p.Address == X);
        var unvaried = new VariedValue.RawJson(RawJson.String(model.ValueOf(part)!));

        var (token, proof) = Sent(model, part, unvaried, CopyKind.ReSigned);

        Assert.Equal(JwsBuilder.Thumbprint(dpop), Jkt(token));
        Assert.Equal(JwsBuilder.Ath(token), (string?)JwsBuilder.Segment(proof, 1)["ath"]);
    }

    [Fact]
    public void R14_10_AVariedCoordinateRebindsTheTokenToAnotherKey()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = JwsBuilder.Thumbprint(dpop);
        var model = Model(issuer, dpop, issuedFor: original);
        var part = model.Parts().Single(p => p.Address == X);
        var exemplar = model.ValueOf(part)!;
        var varied = Variations.PerturbFirst(exemplar);
        Assert.False(Base64Url.DecodeFromChars(varied).AsSpan().SequenceEqual(Base64Url.DecodeFromChars(exemplar)), "the variation did not change the decoded bytes");

        var value = Variations.Closed.Single(v => v.Id == "perturbed-first").Make(part, exemplar);
        var (token, _) = Sent(model, part, value, CopyKind.ReSigned);

        Assert.NotEqual(original, Jkt(token));
    }

    [Fact]
    public void R14_10_TheUnsignedCopyKeepsTheTokenAsIssued()
    {
        using var issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issued = JwsBuilder.Thumbprint(issuer);
        var model = Model(issuer, dpop, issuedFor: issued);
        var part = model.Parts().Single(p => p.Address == X);
        var unvaried = new VariedValue.RawJson(RawJson.String(model.ValueOf(part)!));

        var (token, _) = Sent(model, part, unvaried, CopyKind.Unsigned);

        Assert.Equal(issued, Jkt(token));
        Assert.Equal(issued, (string?)model.Jws["token"].Claims!["cnf"]!["jkt"]);
    }

    private static RequestModel Model(ECDsa issuer, ECDsa dpop, string issuedFor)
    {
        var model = new RequestModel("GET", "/v1/inbox");
        model.Jws["token"] = new JwsEntry(
            new JsonObject { ["alg"] = "ES256", ["typ"] = "at+jwt" },
            new JsonObject { ["sub"] = "agent", ["cnf"] = new JsonObject { ["jkt"] = issuedFor } },
            issuer);
        model.Jws["proof"] = new JwsEntry(
            new JsonObject { ["alg"] = "ES256", ["typ"] = "dpop+jwt", ["jwk"] = JwsBuilder.Jwk(dpop) },
            new JsonObject { ["htm"] = "GET", ["htu"] = string.Empty, ["ath"] = string.Empty },
            dpop);
        model.Headers.Add(new HeaderEntry("Authorization", "DPoP ", [], "token"));
        model.Headers.Add(new HeaderEntry("DPoP", string.Empty, [], "proof"));
        return model;
    }

    private static (string Token, string Proof) Sent(RequestModel model, Part part, VariedValue value, CopyKind copy)
    {
        using var request = model.Render(part, value, copy);
        var authorization = request.Headers.GetValues("Authorization").Single();
        return (authorization["DPoP ".Length..], request.Headers.GetValues("DPoP").Single());
    }

    private static string? Jkt(string token) => (string?)JwsBuilder.Segment(token, 1)["cnf"]!["jkt"];
}
