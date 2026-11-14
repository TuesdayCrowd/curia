using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR N2 (errata G70, R6.66), Task 2: the reader's key set of today (<c>ActaDocuments.ReadLogJwks</c>) reads a document that carries <c>operator_keys</c> as it reads the same document without the
/// member. Client change: none, in this stage; the Rust reader's half is Task 5's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorKeysDocumentTests
{
    private const string Key =
        """{"alg":"ES256","crv":"P-256","kid":"log-1","kty":"EC","use":"sig","x":"PhN6fr_HthJOvXHO3Zj2FVaqW81_6T9SNoY_t1oDdnQ","y":"gPkgk0J-DuEvTXuwA5uKd3emLko2ye3SDzCQz-Gm1yk","valid_from":"2026-09-01T00:00:00Z","log_index":0}""";

    private const string OperatorKey =
        """{"alg":"ES256","crv":"P-256","kid":"operator-1","kty":"EC","use":"sig","x":"PhN6fr_HthJOvXHO3Zj2FVaqW81_6T9SNoY_t1oDdnQ","y":"gPkgk0J-DuEvTXuwA5uKd3emLko2ye3SDzCQz-Gm1yk","valid_from":"2026-09-02T00:00:00Z","log_index":1}""";

    private static string Described(string document)
    {
        var parsed = JsonReader.Parse(System.Text.Encoding.UTF8.GetBytes(document), AdmitLimits.Default).Match(v => v, e => throw new InvalidOperationException(e.Type));
        var set = ActaDocuments.ReadLogJwks(parsed).Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        string One(LogJwk k) => $"{k.Key.Kid}@{k.LogIndex}/{k.ValidFrom}/{k.Key.X}";
        return $"keys [{string.Join(", ", set.Keys.Select(One))}]; declaration_keys {(set.DeclarationKeys is { } d ? $"[{string.Join(", ", d.Select(One))}]" : "absent")}";
    }

    /// <summary>OAS20c: an <c>operator_keys</c> member, whether it lists a key, lists garbage or lists nothing, does not change what the reader reads of <c>keys</c> and <c>declaration_keys</c>.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[" + OperatorKey + "]")]
    [InlineData("""[7, "garbage", {"kid": 3}, {"kid": "x"}]""")]
    [InlineData("""{"not":"an array"}""")]
    public void OAS20c_AnOperatorKeysMemberChangesNothingTheReaderReads(string operatorKeys)
    {
        var without = $$"""{"keys":[{{Key}}],"declaration_keys":[]}""";
        var with = $$"""{"keys":[{{Key}}],"declaration_keys":[],"operator_keys":{{operatorKeys}}}""";

        Assert.Equal(Described(without), Described(with));
    }
}
