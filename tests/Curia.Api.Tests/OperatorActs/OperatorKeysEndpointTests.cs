#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR N2 (errata G70, R6.66), Task 2: <c>GET /v1/log/jwks</c> serves the operator keys the log published, as <c>operator_keys</c>, beside <c>keys</c> and <c>declaration_keys</c> and without
/// moving either. A class fixture of its own, so that the key this test publishes is in no other class's log (OAS20 reads an empty <c>operator_keys</c> in its own).
/// </summary>
[Collection("forum")]
public sealed class OperatorKeysEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Keys, string DeclarationKeys, string OperatorKeys) Members(string body)
    {
        using var doc = JsonDocument.Parse(body[(body.IndexOf(' ') + 1)..]);
        string Raw(string name) => doc.RootElement.TryGetProperty(name, out var v) ? v.GetRawText() : "absent";
        return (Raw("keys"), Raw("declaration_keys"), Raw("operator_keys"));
    }

    /// <summary>
    /// OAS20b: a <c>log.operator-key</c> row in the log is served as one <c>operator_keys</c> entry with its <c>kid</c>, its <c>log_index</c> (the row's position) and its <c>valid_from</c>; the other two
    /// members are what they were before the row.
    /// </summary>
    [Fact]
    public async Task OAS20b_ThePublishedOperatorKeyIsServedAndTheOtherMembersDoNotMove()
    {
        var api = new OperatorActsApi(forum);
        var before = Members(await api.GetBodyAsync("/v1/log/jwks", Ct));

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: false);
        var jwk = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["alg"] = "ES256", ["crv"] = "P-256", ["kid"] = "operator-key-test", ["kty"] = "EC", ["use"] = "sig",
            ["x"] = Base64Url.EncodeToString(p.Q.X!), ["y"] = Base64Url.EncodeToString(p.Q.Y!),
        });
        var position = await api.EventCountAsync(Ct);
        await api.InsertRowAsync("log.operator-key", "log:operator-keys", $$"""{"kid":"operator-key-test","jwk":{{jwk}},"valid_from":"2026-09-01T00:00:00Z"}""", "operator:ops", forum.Now, Ct);

        var body = await api.GetBodyAsync("/v1/log/jwks", Ct);
        Assert.StartsWith("200 ", body, StringComparison.Ordinal);
        var after = Members(body);
        using var served = JsonDocument.Parse(after.OperatorKeys);
        var entry = Assert.Single(served.RootElement.EnumerateArray());
        WholeText.Same(
            $"kid operator-key-test; log_index {position}; valid_from 2026-09-01T00:00:00Z; keys and declaration_keys unchanged",
            $"kid {entry.GetProperty("kid").GetString()}; log_index {entry.GetProperty("log_index").GetInt64()}; valid_from {entry.GetProperty("valid_from").GetString()}; "
            + (before.Keys == after.Keys && before.DeclarationKeys == after.DeclarationKeys ? "keys and declaration_keys unchanged" : $"keys {before.Keys} -> {after.Keys}; declaration_keys {before.DeclarationKeys} -> {after.DeclarationKeys}"));
    }
}
